using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace ScheduleApp.Native;

/// <summary>Kết quả kiểm chữ ký số Authenticode nhúng trong file exe/dll.</summary>
internal sealed record SignatureInfo(bool Valid, string Thumbprint, string Subject, string Error);

/// <summary>
/// Kiểm chữ ký số Authenticode nhúng trong file bằng WinVerifyTrust (không kiểm thu hồi chứng chỉ → không cần mạng).
/// File chỉ được ký qua catalog của Windows (nhiều file hệ thống) được coi là chưa ký — bản cập nhật luôn là file ký nhúng.
/// </summary>
internal static class Authenticode
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10, WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010);
    private const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
    private const int CERT_E_EXPIRED = unchecked((int)0x800B0101);
    private const int TRUST_E_EXPLICIT_DISTRUST = unchecked((int)0x800B0111);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    /// <summary>Chữ ký nhúng của <paramref name="file"/>: hợp lệ hay không, chứng chỉ người ký (thumbprint, subject).</summary>
    public static SignatureInfo Inspect(string file)
    {
        if (!File.Exists(file)) return new SignatureInfo(false, "", "", "không thấy file");
        var path = Marshal.StringToHGlobalUni(Path.GetFullPath(file));
        var fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path }, fileInfo, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfo,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = GenericVerifyV2;
            int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (result != 0) return new SignatureInfo(false, "", "", Describe(result));

            // Chữ ký đã được WinVerifyTrust xác nhận — đọc chứng chỉ người ký để so sánh giữa hai bản.
#pragma warning disable SYSLIB0057 // chỉ đọc chứng chỉ người ký trong chữ ký Authenticode, không có API thay thế
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            return new SignatureInfo(true, cert.Thumbprint, cert.Subject, "");
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            return new SignatureInfo(false, "", "", ex.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfo);
            Marshal.FreeHGlobal(path);
        }
    }

    private static string Describe(int hr) => hr switch
    {
        TRUST_E_NOSIGNATURE => "không có chữ ký số",
        TRUST_E_BAD_DIGEST => "file đã bị sửa sau khi ký",
        CERT_E_UNTRUSTEDROOT => "chứng chỉ không được Windows tin cậy",
        CERT_E_EXPIRED => "chứng chỉ đã hết hạn",
        TRUST_E_EXPLICIT_DISTRUST => "chứng chỉ bị chặn",
        _ => $"chữ ký không hợp lệ (mã 0x{hr:X8})"
    };
}
