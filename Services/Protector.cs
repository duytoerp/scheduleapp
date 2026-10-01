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

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        return Prefix + Convert.ToBase64String(Crypt(Encoding.UTF8.GetBytes(plain), protect: true));
    }

    /// <summary>Giải mã; chuỗi không có tiền tố "dpapi:" được coi là chưa mã hóa và trả về nguyên văn.</summary>
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        return Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(stored[Prefix.Length..]), protect: false));
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
