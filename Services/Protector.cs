using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ScheduleApp.Services;

/// <summary>
/// Mã hóa chuỗi bằng Windows DPAPI theo tài khoản người dùng hiện tại — chỉ chính tài khoản này trên máy này giải mã được.
/// </summary>
public static class Protector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "ScheduleApp.Secrets.v1"u8.ToArray();

    /// <summary>Chuỗi đã mã hóa từng không giải mã được — mỗi chuỗi chỉ cảnh báo một lần (giao diện gọi Unprotect liên tục).</summary>
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        return Prefix + Convert.ToBase64String(Crypt(Encoding.UTF8.GetBytes(plain), protect: true));
    }

    /// <summary>Chuỗi đã mã hóa (có tiền tố "dpapi:").</summary>
    public static bool IsProtected(string? stored) => stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Mã hóa nếu còn là chữ thường (dữ liệu cũ / sửa tay trong file); đã mã hóa hoặc rỗng thì giữ nguyên — không mã hóa chồng.
    /// </summary>
    public static string EnsureProtected(string? value) =>
        string.IsNullOrEmpty(value) ? "" : IsProtected(value) ? value : Protect(value);

    /// <summary>
    /// Giải mã; chuỗi không có tiền tố "dpapi:" được coi là chưa mã hóa và trả về nguyên văn. Không giải mã được (dữ liệu chép từ
    /// máy / tài khoản Windows khác, chuỗi hỏng) → trả về "" và ghi một cảnh báo vào nhật ký, không báo lỗi.
    /// </summary>
    public static string Unprotect(string stored) => TryUnprotect(stored, out var plain) ? plain : "";

    /// <summary>Như <see cref="Unprotect"/> nhưng cho biết có giải mã được không (false → <paramref name="plain"/> = "").</summary>
    public static bool TryUnprotect(string stored, out string plain)
    {
        plain = "";
        if (string.IsNullOrEmpty(stored)) return true;
        if (!IsProtected(stored))
        {
            plain = stored;
            return true;
        }
        try
        {
            plain = Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(stored[Prefix.Length..]), protect: false));
            return true;
        }
        catch (Exception ex) when (ex is FormatException or Win32Exception)
        {
            bool first;
            lock (Warned) first = Warned.Add(stored);
            if (first)
                Log.Warn("Không giải mã được dữ liệu đã lưu (có thể chép từ máy / tài khoản Windows khác) — hãy nhập lại mật khẩu / token đó trong ⚙ Cài đặt hoặc 🔑 Bí mật.");
            return false;
        }
    }

    private static byte[] Crypt(byte[] data, bool protect)
    {
        var input = Blob.From(data);
        var entropy = Blob.From(Entropy);
        var output = default(DATA_BLOB);
        try
        {
            bool ok = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), protect ? "Không mã hóa được dữ liệu." : "Không giải mã được dữ liệu (khác tài khoản Windows?).");
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
            Marshal.FreeHGlobal(entropy.pbData);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private static class Blob
    {
        public static DATA_BLOB From(byte[] data)
        {
            var ptr = Marshal.AllocHGlobal(Math.Max(1, data.Length));
            Marshal.Copy(data, 0, ptr, data.Length);
            return new DATA_BLOB { cbData = data.Length, pbData = ptr };
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? description, ref DATA_BLOB entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, ref DATA_BLOB entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}
