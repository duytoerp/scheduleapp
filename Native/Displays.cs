using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ScheduleApp.Native;

/// <param name="Number">Số Windows đặt cho màn hình (\\.\DISPLAY2 → 2). Có thể khác số trong Cài đặt Windows → Màn hình
/// (card đồ họa kép, dock) và đổi sau khi cắm lại — nhận đúng màn hình thật bằng <paramref name="Id"/>.</param>
/// <param name="Bounds">Toàn bộ màn hình (pixel thật).</param>
/// <param name="WorkingArea">Phần không bị thanh tác vụ che.</param>
/// <param name="Id">Mã cố định của màn hình thật (<see cref="Displays.MonitorKey"/>); null = không đọc được.</param>
internal sealed record Display(int Number, Rectangle Bounds, Rectangle WorkingArea, bool Primary, string? Id = null);

/// <summary>
/// Chọn màn hình khi máy có nhiều màn hình (vd phát video ở màn hình chiếu / TV, máy chính vẫn làm việc bình thường).
/// Lựa chọn lưu dạng số: 0 = màn hình chính, -1 = màn hình đang có chuột, N ≥ 1 = màn hình số N — kèm mã màn hình
/// (<see cref="Display.Id"/>) để vẫn phát đúng màn hình đó khi số đổi.
/// </summary>
internal static partial class Displays
{
    public const int Primary = 0;
    public const int UnderMouse = -1;

    /// <summary>Kiểm thử: dùng danh sách màn hình này thay cho các màn hình thật.</summary>
    internal static IReadOnlyList<Display>? TestDisplays { get; set; }

    [GeneratedRegex(@"(\d+)\s*$")]
    private static partial Regex TrailingNumber();

    /// <summary>Các màn hình đang cắm, theo số của Windows.</summary>
    public static List<Display> All()
    {
        if (TestDisplays is { } fake) return [.. fake];
        var screens = Screen.AllScreens;
        var ids = MonitorIds();
        var list = new List<Display>();
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            var m = TrailingNumber().Match(s.DeviceName);
            int number = m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > 0 ? n : i + 1;
            // Hai màn hình trùng số (hiếm) → số kế tiếp chưa dùng.
            while (list.Any(d => d.Number == number)) number++;
            list.Add(new Display(number, s.Bounds, s.WorkingArea, s.Primary, ids.GetValueOrDefault(s.DeviceName)));
        }
        return [.. list.OrderBy(d => d.Number)];
    }

    /// <summary>
    /// Mã cố định của màn hình thật: hãng + mẫu trong EDID và cổng trên card đồ họa, vd "GSM5BB2#UID4353" (giống mã thiết bị
    /// DISPLAY\GSM5BB2\…&amp;UID4353 trong Device Manager). Không đổi khi Windows đánh lại số \\.\DISPLAYn; đổi khi cắm sang cổng khác.
    /// </summary>
    internal static string MonitorKey(ushort edidManufacturer, ushort edidProduct, bool edidValid, string? friendlyName, uint targetId)
    {
        var monitor = "";
        if (edidValid)
        {
            // Mã hãng trong EDID: 2 byte big-endian, 3 chữ cái × 5 bit (1 = A).
            int v = (edidManufacturer >> 8) | ((edidManufacturer & 0xFF) << 8);
            char[] letters = [(char)('@' + ((v >> 10) & 31)), (char)('@' + ((v >> 5) & 31)), (char)('@' + (v & 31))];
            monitor = (letters.All(c => c is >= 'A' and <= 'Z') ? new string(letters) : edidManufacturer.ToString("X4", CultureInfo.InvariantCulture))
                      + edidProduct.ToString("X4", CultureInfo.InvariantCulture);
        }
        else if (!string.IsNullOrWhiteSpace(friendlyName)) monitor = friendlyName.Trim();
        return (monitor.Length > 0 ? monitor + "#" : "") + "UID" + targetId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Mã màn hình thật (<see cref="MonitorKey"/>) của từng \\.\DISPLAYn đang dùng, đọc từ cấu hình hiển thị của Windows (QueryDisplayConfig).
    /// Không dùng EnumDisplayDevices: màn hình DisplayPort đang ngủ / tắt vẫn còn trong cấu hình hiển thị nhưng không còn thiết bị
    /// màn hình để đọc đường dẫn — đúng lúc lịch chạy phát video khi không có ai ngồi máy.
    /// </summary>
    private static Dictionary<string, string> MonitorIds()
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Vừa cắm / rút màn hình giữa hai lần gọi → thiếu chỗ, đọc lại.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Win32.GetDisplayConfigBufferSizes(Win32.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0) break;
            var paths = new Win32.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new Win32.DISPLAYCONFIG_MODE_INFO[modeCount];
            int error = Win32.QueryDisplayConfig(Win32.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (error == Win32.ERROR_INSUFFICIENT_BUFFER) continue;
            if (error != 0) break;
            foreach (var p in paths.Take((int)pathCount))
            {
                var source = new Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = Header(Win32.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, Marshal.SizeOf<Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), p.sourceAdapterId, p.sourceId)
                };
                var target = new Win32.DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = Header(Win32.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME, Marshal.SizeOf<Win32.DISPLAYCONFIG_TARGET_DEVICE_NAME>(), p.targetAdapterId, p.targetId)
                };
                if (Win32.DisplayConfigGetDeviceInfo(ref source) != 0 || Win32.DisplayConfigGetDeviceInfo(ref target) != 0
                    || string.IsNullOrEmpty(source.viewGdiDeviceName)) continue;
                var key = MonitorKey(target.edidManufactureId, target.edidProductCodeId,
                    (target.flags & Win32.DISPLAYCONFIG_TARGET_EDID_IDS_VALID) != 0, target.monitorFriendlyDeviceName, p.targetId);
                // Chế độ nhân bản (một màn hình Windows hiện trên nhiều màn hình thật): lấy mã nhỏ nhất cho ổn định.
                if (!ids.TryGetValue(source.viewGdiDeviceName, out var other) || string.CompareOrdinal(key, other) < 0)
                    ids[source.viewGdiDeviceName] = key;
            }
            break;
        }
        return ids;
    }

    private static Win32.DISPLAYCONFIG_DEVICE_INFO_HEADER Header(int type, int size, Win32.LUID adapter, uint id) =>
        new() { type = type, size = size, adapterId = adapter, id = id };

    /// <summary>
    /// Màn hình ứng với lựa chọn <paramref name="choice"/>. Màn hình số N không còn cắm (vd rút máy chiếu) → màn hình chính,
    /// kèm cảnh báo để ghi nhật ký.
    /// </summary>
    public static (Display Display, string? Warning) Pick(int choice, IReadOnlyList<Display> displays, Point mouse)
    {
        if (displays.Count == 0) throw new InvalidOperationException("Không tìm thấy màn hình nào.");
        var primary = displays.FirstOrDefault(d => d.Primary) ?? displays[0];
        if (choice == UnderMouse)
            return (displays.FirstOrDefault(d => d.Bounds.Contains(mouse)) ?? primary, null);
        if (choice <= Primary) return (primary, null);
        var match = displays.FirstOrDefault(d => d.Number == choice);
        return match != null
            ? (match, null)
            : (primary, $"không thấy màn hình {choice} (máy đang có {displays.Count} màn hình) — phát ở màn hình chính");
    }

    /// <summary>
    /// Như trên, nhưng ưu tiên màn hình có mã <paramref name="id"/> đã lưu lúc chọn: số màn hình có thể đổi sau khi cắm lại / đổi dock,
    /// mã thì không. Không thấy mã (đã rút, cắm sang cổng khác) → theo số như trên, kèm cảnh báo.
    /// <c>Note</c>: màn hình đã chọn nay mang số khác (để ghi nhật ký).
    /// </summary>
    public static (Display Display, string? Warning, string? Note) Pick(int choice, string? id, IReadOnlyList<Display> displays, Point mouse)
    {
        if (choice <= Primary || string.IsNullOrEmpty(id))
        {
            var (display, warning) = Pick(choice, displays, mouse);
            return (display, warning, null);
        }
        if (FindById(id, displays) is { } same)
            return (same, null, same.Number == choice ? null : $"màn hình đã chọn nay là màn hình {same.Number} (lúc chọn là màn hình {choice})");
        var (byNumber, missing) = Pick(choice, displays, mouse);
        return (byNumber, missing ?? $"không thấy màn hình đã chọn (đã rút hoặc cắm sang cổng khác?) — phát ở màn hình {choice} hiện có", null);
    }

    public static (Display Display, string? Warning, string? Note) Pick(int choice, string? id) => Pick(choice, id, All(), Cursor.Position);

    /// <summary>Màn hình đang cắm có mã <paramref name="id"/> (không phân biệt hoa thường); null khi không có.</summary>
    public static Display? FindById(string? id, IReadOnlyList<Display> displays) =>
        string.IsNullOrEmpty(id) ? null : displays.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Trình phát có giành bàn phím (Esc / Space / →) không: chỉ khi máy có một màn hình, hoặc màn hình phát đang có chuột hay cửa sổ
    /// người dùng đang dùng. Phát ở màn hình khác (máy chiếu) trong lúc người dùng gõ ở màn hình chính thì không lấy phím của họ —
    /// Space làm video tạm dừng, Esc dừng bước, chữ đang gõ bị mất.
    /// </summary>
    /// <param name="screen">Toàn bộ màn hình phát.</param>
    /// <param name="foreground">Khung cửa sổ đang được chọn (null = không có) — tính theo điểm giữa cửa sổ.</param>
    public static bool TakesFocus(Rectangle screen, Point cursor, Rectangle? foreground, int screenCount) =>
        screenCount <= 1 || screen.Contains(cursor) ||
        (foreground is { IsEmpty: false } f && screen.Contains(f.X + f.Width / 2, f.Y + f.Height / 2));

    /// <summary>Như trên, với vị trí chuột, cửa sổ đang được chọn và số màn hình lúc này.</summary>
    public static bool TakesFocus(Display screen)
    {
        var window = Win32.GetForegroundWindow();
        Rectangle? foreground = window != IntPtr.Zero && Win32.GetWindowRect(window, out var r) ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : null;
        return TakesFocus(screen.Bounds, Cursor.Position, foreground, All().Count);
    }

    /// <summary>Tên lựa chọn để hiện trong mô tả bước.</summary>
    public static string ChoiceName(int choice) => choice switch
    {
        UnderMouse => "màn hình đang có chuột",
        <= Primary => "màn hình chính",
        _ => $"màn hình {choice}"
    };

    /// <summary>"Màn hình 2 — 1920×1080, bên phải" (vị trí so với màn hình chính).</summary>
    public static string Label(Display d, IReadOnlyList<Display> all)
    {
        var text = $"Màn hình {d.Number} — {d.Bounds.Width}×{d.Bounds.Height}";
        if (d.Primary) return text + " (chính)";
        var primary = all.FirstOrDefault(x => x.Primary);
        if (primary == null) return text;
        string? side = d.Bounds.Left >= primary.Bounds.Right ? "bên phải"
            : d.Bounds.Right <= primary.Bounds.Left ? "bên trái"
            : d.Bounds.Bottom <= primary.Bounds.Top ? "phía trên"
            : d.Bounds.Top >= primary.Bounds.Bottom ? "phía dưới"
            : null;
        return side != null ? $"{text}, {side}" : text;
    }

    /// <summary>Khung cửa sổ phát video trên màn hình <paramref name="d"/>: toàn màn hình = cả màn hình; cửa sổ thường = 2/3 vùng làm việc, ở giữa.</summary>
    public static Rectangle PlayerRect(Display d, bool fullscreen)
    {
        if (fullscreen) return d.Bounds;
        var area = d.WorkingArea;
        int w = Math.Max(320, area.Width * 2 / 3), h = Math.Max(240, area.Height * 2 / 3);
        w = Math.Min(w, area.Width);
        h = Math.Min(h, area.Height);
        return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }
}
