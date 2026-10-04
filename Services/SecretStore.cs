using System.Text.Json;
using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>
/// Kho bí mật (mật khẩu, token…) dùng trong flow qua {{secret:Tên}}. Lưu ở secrets.json, giá trị mã hóa DPAPI,
/// không nằm trong jobs.json nên không bị lộ khi xuất/chia sẻ công việc.
/// </summary>
public static class SecretStore
{
    private static readonly object Sync = new();
    private static Dictionary<string, string>? _items;

    private static string FilePath => Path.Combine(JobStore.DataDir, "secrets.json");

    /// <summary>
    /// Giá trị đã mã hóa theo tên. Đọc lần đầu: file hỏng → giữ bản .broken-…, dùng bản .bak (không để lần lưu sau xóa sạch bí mật cũ).
    /// Giá trị không giải mã được (chép từ máy khác) vẫn được giữ nguyên khi lưu.
    /// </summary>
    private static Dictionary<string, string> Items
    {
        get
        {
            if (_items != null) return _items;
            var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var loaded = SafeFile.Load(FilePath, "bí mật", "danh sách bí mật đang trống, hãy nhập lại trong mục 🔑 Bí mật",
                json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonDefaults.Options));
            if (loaded.Value != null)
                foreach (var (k, v) in loaded.Value) items[k] = v;
            return _items = items;
        }
    }

    /// <summary>Đọc secrets.json ngay lúc mở ứng dụng để file hỏng (nếu có) được báo cùng các file dữ liệu khác.</summary>
    public static void EnsureLoaded()
    {
        lock (Sync) _ = Items;
    }

    /// <summary>Bỏ bản đã đọc trong bộ nhớ để lần sau đọc lại từ file (chỉ dùng cho kiểm thử).</summary>
    internal static void ResetForTests()
    {
        lock (Sync) _items = null;
    }

    public static IReadOnlyList<string> Names
    {
        get
        {
            lock (Sync) return Items.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public static bool Contains(string name)
    {
        lock (Sync) return Items.ContainsKey(name.Trim());
    }

    /// <summary>
    /// Giá trị đã giải mã, hoặc null nếu chưa có. Không giải mã được (chép từ máy / tài khoản Windows khác) → báo lỗi rõ ràng
    /// để bước dùng {{secret:…}} dừng lại, không âm thầm gõ / gửi chuỗi rỗng.
    /// </summary>
    public static string? Get(string name)
    {
        lock (Sync)
        {
            if (!Items.TryGetValue(name.Trim(), out var stored)) return null;
            return Protector.TryUnprotect(stored, out var plain) ? plain
                : throw new InvalidOperationException($"Không giải mã được bí mật \"{name.Trim()}\" (có thể chép từ máy / tài khoản Windows khác) — hãy nhập lại trong mục 🔑 Bí mật.");
        }
    }

    public static void Set(string name, string value)
    {
        lock (Sync)
        {
            Items[name.Trim()] = Protector.Protect(value);
            Save();
        }
    }

    public static void Remove(string name)
    {
        lock (Sync)
        {
            if (Items.Remove(name.Trim())) Save();
        }
    }

    /// <summary>Lưu nguyên các giá trị đã mã hóa (kể cả giá trị không giải mã được trên máy này).</summary>
    private static void Save() =>
        SafeFile.WriteAllText(FilePath, JsonSerializer.Serialize(Items, JsonDefaults.Options));
}
