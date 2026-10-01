using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScheduleApp.Services;

/// <summary>Thông tin bản mới tìm được.</summary>
public sealed record UpdateInfo(Version Version, string DownloadUrl, string Notes, string? Sha256, bool FromGitHub, string Token = "");

/// <summary>
/// Kiểm tra và cài bản mới của ScheduleApp.exe (bản publish một file). Nguồn:
/// "github:chủ/repo" (GitHub Releases, repo riêng tư cần token), URL tới version.json, hoặc thư mục dùng chung chứa version.json.
/// version.json: {"version":"2.1.0","url":"ScheduleApp.exe","notes":"…","sha256":"…"} — url tương đối tính theo vị trí version.json.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static Version Current =>
        typeof(UpdateService).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>Bản mới hơn bản đang chạy, hoặc null nếu đang dùng bản mới nhất.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct, Models.UpdateSettings? settings = null)
    {
        var s = settings ?? SettingsStore.Current.Update;
        var source = s.Source.Trim();
        if (source.Length == 0) throw new InvalidOperationException("Chưa nhập nguồn cập nhật (⚙ Cài đặt → Chung).");

        var info = GitHubRepo(source) is { } repo
            ? await CheckGitHubAsync(repo, Protector.Unprotect(s.Token), ct)
            : await CheckManifestAsync(source, ct);
        return info.Version > Current ? info : null;
    }

    /// <summary>"github:chủ/repo" hoặc "https://github.com/chủ/repo" → "chủ/repo".</summary>
    private static string? GitHubRepo(string source)
    {
        if (source.StartsWith("github:", StringComparison.OrdinalIgnoreCase)) return source[7..].Trim().Trim('/');
        if (Uri.TryCreate(source, UriKind.Absolute, out var u) && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = u.AbsolutePath.Trim('/').Split('/');
            if (parts.Length >= 2) return parts[0] + "/" + parts[1].Replace(".git", "");
        }
        return null;
    }

    private static HttpRequestMessage GitHubRequest(string url, string token, string accept)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("ScheduleApp-Updater");
        req.Headers.Accept.ParseAdd(accept);
        if (token.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static async Task<UpdateInfo> CheckGitHubAsync(string repo, string token, CancellationToken ct)
    {
        using var req = GitHubRequest($"https://api.github.com/repos/{repo}/releases/latest", token, "application/vnd.github+json");
        using var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if ((int)resp.StatusCode == 404)
            throw new InvalidOperationException(token.Length == 0
                ? $"Không thấy bản phát hành của {repo} — repo riêng tư cần nhập token GitHub (quyền đọc Contents)."
                : $"Repo {repo} chưa có bản phát hành (Release) nào, hoặc token không có quyền đọc.");
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub trả về {(int)resp.StatusCode}: {ApiClient.Short(json)}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = ParseVersion(root.GetProperty("tag_name").GetString() ?? "");
        var assets = root.GetProperty("assets").EnumerateArray().ToList();
        var exe = assets.FirstOrDefault(a => string.Equals(a.GetProperty("name").GetString(), "ScheduleApp.exe", StringComparison.OrdinalIgnoreCase));
        if (exe.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"Bản phát hành {version} không có file ScheduleApp.exe.");
        string? sha = exe.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:") ? digest[7..] : null;
        return new UpdateInfo(version, exe.GetProperty("url").GetString()!, root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", sha, true, token);
    }

    private static async Task<UpdateInfo> CheckManifestAsync(string source, CancellationToken ct)
    {
        bool web = source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        string manifestPath = web || source.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? source : Path.Combine(source, "version.json");
        string json = web
            ? await Http.GetStringAsync(manifestPath, ct)
            : File.Exists(manifestPath) ? await File.ReadAllTextAsync(manifestPath, ct) : throw new FileNotFoundException($"Không thấy \"{manifestPath}\".");

        using var doc = JsonDocument.Parse(json.TrimStart('﻿')); // version.json lưu bằng Notepad có thể kèm BOM
        var root = doc.RootElement;
        var version = ParseVersion(root.GetProperty("version").GetString() ?? "");
        var url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "ScheduleApp.exe" : "ScheduleApp.exe";
        // Đường dẫn tương đối tính theo vị trí của version.json.
        if (web) url = new Uri(new Uri(manifestPath), url).ToString();
        else if (!Path.IsPathRooted(url)) url = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, url);
        return new UpdateInfo(version, url,
            root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "",
            root.TryGetProperty("sha256", out var h) ? h.GetString() : null, false);
    }

    private static Version ParseVersion(string text)
    {
        var t = text.Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(['-', '+', ' ']);
        if (dash > 0) t = t[..dash];
        if (!Version.TryParse(t.Contains('.') ? t : t + ".0", out var v)) throw new InvalidOperationException($"Số phiên bản \"{text}\" không hợp lệ.");
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>Có tự thay file exe đang chạy được không (bản publish một file, thư mục ghi được).</summary>
    public static bool CanSelfUpdate(out string reason)
    {
        var exe = Environment.ProcessPath;
        if (exe == null || !Path.GetFileName(exe).Equals("ScheduleApp.exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Đang chạy bản debug (dotnet run) — chỉ bản publish ScheduleApp.exe mới tự cập nhật được.";
            return false;
        }
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "ScheduleApp.dll")))
        {
            reason = "Bản đang chạy không phải bản một file (có ScheduleApp.dll bên cạnh) — hãy cài bằng bộ cài hoặc bản publish một file.";
            return false;
        }
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(exe)!, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            reason = $"Không có quyền ghi vào \"{Path.GetDirectoryName(exe)}\" — cài vào thư mục của người dùng hoặc chạy bộ cài mới.";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>Tải file exe mới về thư mục tạm, kiểm tra SHA-256 nếu có.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<int>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ScheduleApp-update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "ScheduleApp.exe");

        if (!info.FromGitHub && !info.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(info.DownloadUrl, target, true);
        }
        else
        {
            using var req = info.FromGitHub
                ? GitHubRequest(info.DownloadUrl, info.Token, "application/octet-stream")
                : new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(target);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((int)(done * 100 / total));
            }
        }

        if (!string.IsNullOrWhiteSpace(info.Sha256))
        {
            await using var fs = File.OpenRead(target);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!hash.Equals(info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                fs.Close();
                File.Delete(target);
                throw new InvalidOperationException("File tải về không khớp mã SHA-256 — đã hủy cập nhật.");
            }
        }
        return target;
    }

    /// <summary>
    /// Chạy script thay file exe sau khi ScheduleApp thoát rồi mở lại. Người gọi phải thoát ứng dụng ngay sau đó.
    /// Bản cũ được giữ thành ScheduleApp.exe.old (xóa ở lần khởi động sau) để khôi phục nếu thay thất bại.
    /// </summary>
    public static void ApplyAndRestart(string newExe)
    {
        var script = Path.Combine(Path.GetDirectoryName(newExe)!, "update.cmd");
        File.WriteAllText(script, BuildUpdateScript(newExe, Environment.ProcessPath!, Environment.ProcessId, restart: true), new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        Log.Info("Đang cập nhật lên bản mới — ScheduleApp sẽ tự mở lại.");
    }

    /// <summary>Script cmd: chờ tiến trình <paramref name="waitPid"/> thoát, thay file (giữ bản cũ .old, khôi phục nếu lỗi), mở lại, tự xóa.</summary>
    internal static string BuildUpdateScript(string newExe, string targetExe, int waitPid, bool restart)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine($"set PID={waitPid}");
        sb.AppendLine(":wait");
        // Đường dẫn đầy đủ: PATH có thể chứa find/ping của Git for Windows (Unix) đứng trước bản của Windows.
        // ping thay cho timeout: timeout báo lỗi khi không có bàn phím (cửa sổ ẩn).
        sb.AppendLine("set SYS=%SystemRoot%\\System32");
        sb.AppendLine("\"%SYS%\\tasklist.exe\" /FI \"PID eq %PID%\" /NH 2>nul | \"%SYS%\\find.exe\" \" %PID% \" >nul && (\"%SYS%\\PING.EXE\" -n 2 127.0.0.1 >nul & goto wait)");
        sb.AppendLine($"move /y \"{targetExe}\" \"{targetExe}.old\" >nul");
        sb.AppendLine($"move /y \"{newExe}\" \"{targetExe}\" >nul || move /y \"{targetExe}.old\" \"{targetExe}\" >nul");
        if (restart) sb.AppendLine($"start \"\" \"{targetExe}\"");
        sb.AppendLine("del \"%~f0\"");
        return sb.ToString();
    }

    /// <summary>Xóa bản cũ còn sót lại sau lần cập nhật trước.</summary>
    public static void CleanupOldVersion()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && File.Exists(exe + ".old")) File.Delete(exe + ".old");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
