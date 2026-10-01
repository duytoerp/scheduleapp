using System.Security.Cryptography;

namespace ScheduleApp.Automation;

/// <summary>
/// Mã xác thực một lần theo thời gian (TOTP, RFC 6238) — như ứng dụng Microsoft / Google Authenticator tạo ra,
/// dùng để tự đăng nhập tài khoản test có MFA. Khóa bí mật dạng Base32 (chuỗi hiện khi thêm "ứng dụng xác thực khác").
/// </summary>
internal static class Totp
{
    /// <summary>Mã <paramref name="digits"/> chữ số cho thời điểm <paramref name="time"/> (bước 30 giây, HMAC-SHA1).</summary>
    public static string Code(string base32Secret, DateTimeOffset time, int digits = 6, int stepSeconds = 30) =>
        Code(DecodeBase32(base32Secret), time.ToUnixTimeSeconds() / stepSeconds, digits);

    /// <summary>Mã cho bộ đếm <paramref name="counter"/> (HOTP, RFC 4226).</summary>
    public static string Code(byte[] key, long counter, int digits = 6)
    {
        var msg = new byte[8];
        for (int i = 7; i >= 0; i--)
        {
            msg[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }
        var hash = HMACSHA1.HashData(key, msg);
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        int mod = (int)Math.Pow(10, digits);
        return (binary % mod).ToString(new string('0', digits));
    }

    /// <summary>Số giây còn lại của mã hiện tại (dưới vài giây thì nên chờ mã mới để không bị hết hạn khi gửi).</summary>
    public static int SecondsLeft(DateTimeOffset time, int stepSeconds = 30) => stepSeconds - (int)(time.ToUnixTimeSeconds() % stepSeconds);

    /// <summary>Giải mã Base32 (RFC 4648), bỏ qua khoảng trắng, gạch nối và dấu '=', không phân biệt hoa thường.</summary>
    public static byte[] DecodeBase32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = new string(text.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '=').Select(char.ToUpperInvariant).ToArray());
        if (clean.Length == 0) throw new FormatException("Khóa TOTP trống.");
        var bytes = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            int v = alphabet.IndexOf(c);
            if (v < 0) throw new FormatException($"Khóa TOTP không hợp lệ (ký tự \"{c}\" không thuộc Base32 A–Z, 2–7).");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
                buffer &= (1 << bits) - 1;
            }
        }
        return [.. bytes];
    }
}
