using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScheduleApp.Native;

namespace ScheduleApp.Services;

/// <summary>Thông tin bản mới tìm được. <paramref name="HashSource"/>: mã SHA-256 lấy từ đâu (digest của GitHub, version.json).</summary>
public sealed record UpdateInfo(Version Version, string DownloadUrl, string Notes, string? Sha256, bool FromGitHub, string Token = "", string HashSource = "");

/// <summary>File bản mới đã tải về thư mục tạm riêng và đã qua mọi bước kiểm tra (SHA-256, phiên bản, chữ ký số).</summary>
public sealed record UpdateDownload(string File, string Directory, Version Version, string Sha256, bool Signed, string Signature);

/// <summary>
/// Kiểm tra và cài bản mới của ScheduleApp.exe (bản publish một file). Nguồn:
/// "github:chủ/repo" (GitHub Releases, repo riêng tư cần token), URL https tới version.json, hoặc thư mục dùng chung chứa version.json.
/// version.json: {"version":"2.1.0","url":"ScheduleApp.exe","notes":"…","sha256":"…"} — url tương đối tính theo vị trí version.json.
/// An toàn: bắt buộc có SHA-256, nguồn qua mạng chỉ dùng https, file tải về phải đúng phiên bản đã báo và mới hơn bản đang chạy,
/// bản đang chạy có chữ ký số thì bản mới phải cùng người ký; bản mới không khởi động được thì script tự quay về bản cũ (cả dữ liệu).
/// </summary>
public static class UpdateService
{
    /// <summary>Biến môi trường script cập nhật truyền cho bản mới: đường dẫn file đánh dấu "đã khởi động xong".</summary>
    internal const string MarkerVariable = "SCHEDULEAPP_UPDATE_MARKER";

    /// <summary>Số giây script chờ bản mới báo khởi động xong trước khi quay về bản cũ.</summary>
    internal const int HealthWaitSeconds = 90;

    /// <summary>Đuôi file ghi chú script để lại cạnh ScheduleApp.exe khi phải quay về bản cũ.</summary>
    internal const string RollbackNoteSuffix = ".update-failed.txt";

    /// <summary>Đuôi file bản mới để lại khi mở lên mà đã có ScheduleApp khác giữ khóa chạy một phiên bản.</summary>
    internal const string SecondInstanceSuffix = ".second";

    /// <summary>HttpClient dùng chung (kiểm thử thay bằng client tin chứng chỉ của máy chủ https cục bộ).</summary>
    internal static HttpClient Http { get; set; } = new() { Timeout = TimeSpan.FromMinutes(10) };

    private const string GitHubApi = "https://api.github.com";

    /// <summary>File đánh dấu khởi động xong của lần mở này (null nếu không phải do script cập nhật mở).</summary>
    private static string? _healthMarker;

    public static Version Current =>
        typeof(UpdateService).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>Bản mới hơn bản đang chạy, hoặc null nếu đang dùng bản mới nhất.</summary>
    public static Task<UpdateInfo?> CheckAsync(CancellationToken ct, Models.UpdateSettings? settings = null) => CheckAsync(ct, settings, GitHubApi);

    /// <summary>Như trên, <paramref name="gitHubApi"/> thay cho https://api.github.com (kiểm thử với máy chủ cục bộ).</summary>
    internal static async Task<UpdateInfo?> CheckAsync(CancellationToken ct, Models.UpdateSettings? settings, string gitHubApi)
    {
        var s = settings ?? SettingsStore.Current.Update;
        var source = s.Source.Trim();
        if (source.Length == 0) throw new InvalidOperationException("Chưa nhập nguồn cập nhật (⚙ Cài đặt → Chung).");

        return GitHubRepo(source) is { } repo
            ? await CheckGitHubAsync(repo, Protector.Unprotect(s.Token), gitHubApi, ct)
            : await CheckManifestAsync(source, ct);
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
        var req = new HttpRequestMessage(HttpMethod.Get, RequireHttps(url));
        req.Headers.UserAgent.ParseAdd("ScheduleApp-Updater");
        req.Headers.Accept.ParseAdd(accept);
        if (token.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static async Task<UpdateInfo?> CheckGitHubAsync(string repo, string token, string api, CancellationToken ct)
    {
        using var req = GitHubRequest($"{api.TrimEnd('/')}/repos/{repo}/releases/latest", token, "application/vnd.github+json");
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
        if (version <= Current) return null;
        var assets = root.GetProperty("assets").EnumerateArray().ToList();
        JsonElement Asset(string name) =>
            assets.FirstOrDefault(a => string.Equals(a.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));
        var exe = Asset("ScheduleApp.exe");
        if (exe.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"Bản phát hành {version} không có file ScheduleApp.exe.");

        // Mã SHA-256: digest GitHub tự ghi cho file; bản phát hành cũ chưa có digest thì lấy từ version.json đi kèm (release.yml tải lên).
        string? sha = exe.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest[7..] : null;
        string hashSource = "digest của GitHub";
        if (!IsSha256(sha) && Asset("version.json") is { ValueKind: JsonValueKind.Object } manifest)
        {
            using var mreq = GitHubRequest(manifest.GetProperty("url").GetString()!, token, "application/octet-stream");
            using var mresp = await Http.SendAsync(mreq, ct);
            if (!mresp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Không tải được version.json của bản phát hành {version} (GitHub trả về {(int)mresp.StatusCode}).");
            using var mdoc = JsonDocument.Parse((await mresp.Content.ReadAsStringAsync(ct)).TrimStart('\uFEFF'));
            if (mdoc.RootElement.TryGetProperty("version", out var mv) && ParseVersion(mv.GetString() ?? "") != version)
                throw new InvalidOperationException($"version.json ghi bản {mv.GetString()} nhưng bản phát hành là {version} — đã từ chối cập nhật.");
            sha = mdoc.RootElement.TryGetProperty("sha256", out var h) ? h.GetString() : null;
            hashSource = "version.json của bản phát hành";
        }
        if (!IsSha256(sha))
            throw new InvalidOperationException($"Bản phát hành {version} không có mã SHA-256 (GitHub không ghi digest, version.json đi kèm cũng không có sha256) — " +
                "không kiểm tra được file tải về nên đã từ chối cập nhật.");
        return new UpdateInfo(version, RequireHttps(exe.GetProperty("url").GetString()!), root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            sha!.Trim(), true, token, hashSource);
    }

    private static async Task<UpdateInfo?> CheckManifestAsync(string source, CancellationToken ct)
    {
        bool web = IsWebSource(source);
        string manifestPath = web || source.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? source : Path.Combine(source, "version.json");
        string json = web
            ? await Http.GetStringAsync(manifestPath, ct)
            : File.Exists(manifestPath) ? await File.ReadAllTextAsync(manifestPath, ct) : throw new FileNotFoundException($"Không thấy \"{manifestPath}\".", manifestPath);

        using var doc = JsonDocument.Parse(json.TrimStart('\uFEFF')); // version.json lưu bằng Notepad có thể kèm BOM
        var root = doc.RootElement;
        var version = ParseVersion(root.GetProperty("version").GetString() ?? "");
        if (version <= Current) return null;
        var url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "ScheduleApp.exe" : "ScheduleApp.exe";
        // Đường dẫn tương đối tính theo vị trí của version.json; file exe qua mạng cũng phải là https.
        if (web) url = RequireHttps(new Uri(new Uri(manifestPath), url).ToString());
        else if (IsWebSource(url)) url = RequireHttps(url);
        else if (!Path.IsPathRooted(url)) url = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, url);
        var sha = root.TryGetProperty("sha256", out var h) ? h.GetString() : null;
        if (!IsSha256(sha))
            throw new InvalidOperationException($"version.json của bản {version} thiếu mã sha256 (64 ký tự hex) — không kiểm tra được file tải về nên đã từ chối cập nhật.");
        return new UpdateInfo(version, url, root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "", sha!.Trim(), false, HashSource: "version.json");
    }

    /// <summary>
    /// Nguồn / đường dẫn qua mạng (true, chỉ chấp nhận https://) hay thư mục, file trên máy / mạng nội bộ (false: C:\…, \\máy\thư mục).
    /// http:// và giao thức khác bị từ chối: không mã hóa, kẻ gian trên đường truyền có thể đánh tráo cả version.json lẫn mã SHA-256.
    /// </summary>
    internal static bool IsWebSource(string source)
    {
        var s = source.Trim();
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Nguồn cập nhật \"{s}\" dùng http:// (không mã hóa, có thể bị đánh tráo file) — hãy dùng https:// hoặc thư mục dùng chung.");
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && !uri.IsFile)
            throw new InvalidOperationException($"Nguồn cập nhật \"{s}\" không được hỗ trợ — dùng github:chủ/repo, https://… hoặc thư mục dùng chung.");
        return false;
    }

    private static string RequireHttps(string url) =>
        IsWebSource(url) ? url : throw new InvalidOperationException($"Địa chỉ tải bản mới \"{url}\" không phải https:// — đã từ chối cập nhật.");

    private static bool IsSha256(string? text) =>
        text != null && text.Trim() is { Length: 64 } t && t.All(Uri.IsHexDigit);

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

    /// <summary>Tải file exe mới về một thư mục tạm riêng (tên ngẫu nhiên, mỗi lần một thư mục) rồi kiểm tra; lỗi thì xóa thư mục đó.</summary>
    public static Task<UpdateDownload> DownloadAsync(UpdateInfo info, IProgress<int>? progress, CancellationToken ct) =>
        DownloadAsync(info, progress, ct, Environment.ProcessPath);

    /// <summary>Như trên, <paramref name="runningExe"/> là file exe đang chạy dùng để so chữ ký số.</summary>
    internal static async Task<UpdateDownload> DownloadAsync(UpdateInfo info, IProgress<int>? progress, CancellationToken ct, string? runningExe)
    {
        if (!IsSha256(info.Sha256))
            throw new InvalidOperationException("Bản mới không có mã SHA-256 — không kiểm tra được file tải về nên đã từ chối cập nhật.");
        if (info.Version <= Current)
            throw new InvalidOperationException($"Bản {info.Version} không mới hơn bản đang chạy ({Current}) — đã từ chối cập nhật.");

        var dir = Path.Combine(Path.GetTempPath(), "ScheduleApp-update-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "ScheduleApp.exe");
        try
        {
            if (!info.FromGitHub && !IsWebSource(info.DownloadUrl))
            {
                File.Copy(info.DownloadUrl, target, true);
            }
            else
            {
                using var req = info.FromGitHub
                    ? GitHubRequest(info.DownloadUrl, info.Token, "application/octet-stream")
                    : new HttpRequestMessage(HttpMethod.Get, RequireHttps(info.DownloadUrl));
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Máy chủ trả về lỗi {(int)resp.StatusCode} khi tải bản mới.");
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

            var (signed, signature) = await Task.Run(() => VerifyDownloaded(target, info.Version, info.Sha256!, runningExe), ct);
            return new UpdateDownload(target, dir, info.Version, info.Sha256!.Trim().ToUpperInvariant(), signed, signature);
        }
        catch
        {
            try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Kiểm tra file đã tải: khớp SHA-256; phiên bản ghi trong file (cách tính như <see cref="Current"/>) đúng bản đã báo và mới hơn bản đang chạy
    /// (chặn file cũ bị đổi nhãn); chữ ký số như <see cref="CheckSignature"/>. Sai thì báo lỗi tiếng Việt.
    /// </summary>
    internal static (bool Signed, string Signature) VerifyDownloaded(string file, Version advertised, string sha256, string? runningExe)
    {
        using (var fs = File.OpenRead(file))
        {
            var hash = Convert.ToHexString(SHA256.HashData(fs));
            if (!hash.Equals(sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("File tải về không khớp mã SHA-256 — đã hủy cập nhật.");
        }

        var fv = FileVersionInfo.GetVersionInfo(file);
        if (string.IsNullOrEmpty(fv.FileVersion))
            throw new InvalidOperationException("File tải về không có thông tin phiên bản — không phải ScheduleApp.exe hợp lệ, đã hủy cập nhật.");
        var actual = new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart);
        if (actual != advertised)
            throw new InvalidOperationException($"File tải về là bản {actual}, không phải bản {advertised} như nguồn cập nhật ghi — đã hủy cập nhật.");
        if (actual <= Current)
            throw new InvalidOperationException($"File tải về là bản {actual}, không mới hơn bản đang chạy ({Current}) — đã hủy cập nhật.");

        return CheckSignature(runningExe, file);
    }

    /// <summary>
    /// Chữ ký số Authenticode: bản đang chạy có chữ ký nhúng (kể cả chữ ký nay đã hết hạn / không còn được tin cậy) thì bản mới phải có
    /// chữ ký hợp lệ của cùng người ký (cùng Subject — gia hạn chứng chỉ đổi thumbprint nhưng giữ Subject); bản đang chạy chưa từng ký
    /// thì chỉ dựa vào SHA-256 (ghi nhật ký). Trả về (bản mới có chữ ký hợp lệ?, mô tả cho người dùng).
    /// </summary>
    internal static (bool Signed, string Text) CheckSignature(string? runningExe, string newExe)
    {
        var running = runningExe != null ? Authenticode.Inspect(runningExe) : new SignatureInfo(false, "", "", "không rõ file đang chạy");
        var fresh = Authenticode.Inspect(newExe);
        if (!running.HasSignature)
        {
            Log.Info("Bản đang chạy chưa ký số — chỉ kiểm SHA-256.");
            return (fresh.Valid, fresh.Valid ? $"bản mới có chữ ký số ({ShortName(fresh.Subject)}); bản đang chạy chưa ký số — chỉ kiểm SHA-256" : "chưa ký số — chỉ kiểm SHA-256");
        }
        if (!fresh.Valid)
            throw new InvalidOperationException($"Bản đang chạy có chữ ký số nhưng file tải về {fresh.Error} — đã hủy cập nhật.");
        if (running.Subject.Length == 0 || !fresh.Subject.Equals(running.Subject, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"File tải về được ký bởi người khác ({ShortName(fresh.Subject)}) với bản đang chạy ({ShortName(running.Subject)}) — đã hủy cập nhật.");
        return (true, $"hợp lệ, cùng người ký với bản đang chạy ({ShortName(fresh.Subject)})");
    }

    /// <summary>"CN=Tên, O=…" → "Tên".</summary>
    private static string ShortName(string subject)
    {
        foreach (var part in subject.Split(','))
            if (part.Trim().StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return part.Trim()[3..].Trim('"');
        return subject;
    }

    /// <summary>Lỗi khi kiểm tra / tải bản mới → câu tiếng Việt dễ hiểu (lỗi của ScheduleApp vốn đã là tiếng Việt).</summary>
    public static string Explain(Exception ex) => ex switch
    {
        JsonException => "Nội dung version.json / phản hồi của máy chủ không phải JSON hợp lệ.",
        KeyNotFoundException => "version.json / phản hồi của máy chủ thiếu thông tin cần thiết (vd \"version\").",
        FileNotFoundException f => $"Không thấy file \"{f.FileName ?? ex.Message}\".",
        DirectoryNotFoundException => "Không truy cập được thư mục chứa bản mới (kiểm tra đường dẫn / mạng nội bộ).",
        UnauthorizedAccessException => "Không có quyền đọc / ghi file khi cập nhật.",
        IOException => "Lỗi đọc / ghi file khi cập nhật: " + ex.Message,
        HttpRequestException { InnerException: AuthenticationException } => "Chứng chỉ HTTPS của máy chủ cập nhật không hợp lệ — đã dừng để tránh tải file bị đánh tráo.",
        HttpRequestException h when h.StatusCode is { } code => $"Máy chủ cập nhật trả về lỗi {(int)code}.",
        HttpRequestException => "Không kết nối được máy chủ cập nhật (kiểm tra mạng / địa chỉ nguồn): " + ex.Message,
        TaskCanceledException or OperationCanceledException => "Hết thời gian chờ hoặc đã hủy khi tải bản mới.",
        _ => ex.Message
    };

    /// <summary>
    /// Chạy script thay file exe sau khi ScheduleApp thoát rồi mở bản mới, chờ bản mới báo khởi động xong (tối đa ~90 giây);
    /// không được thì quay về bản cũ. Người gọi phải thoát ứng dụng ngay sau đó.
    /// </summary>
    public static void ApplyAndRestart(UpdateDownload download)
    {
        var script = Path.Combine(download.Directory, "update.cmd");
        var marker = Path.Combine(download.Directory, "started.ok");
        File.WriteAllText(script, BuildUpdateScript(download.File, Environment.ProcessPath!, Environment.ProcessId, restart: true,
            markerPath: marker, cleanupDir: download.Directory, newVersion: download.Version.ToString(),
            restoreOnRollback: BackupData(Path.Combine(download.Directory, "du-lieu-truoc-cap-nhat"))), new UTF8Encoding(false));
        // Đường dẫn đầy đủ tới cmd của Windows (không phụ thuộc PATH / thư mục hiện tại); /d: bỏ qua AutoRun trong registry.
        Process.Start(new ProcessStartInfo(SystemCmd, $"/d /c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        Log.Info($"Đang cập nhật lên bản {download.Version} (SHA-256 {download.Sha256[..12]}…, chữ ký số: {download.Signature}) — ScheduleApp sẽ tự mở lại.");
    }

    /// <summary>
    /// Chép công việc / cài đặt / bí mật sang <paramref name="backupDir"/> trước khi thay file: bản mới có thể đổi định dạng dữ liệu
    /// (vd mã hóa thêm trường) ngay lúc mở rồi mới hỏng — quay về bản cũ thì script chép trả lại để bản cũ đọc được.
    /// </summary>
    internal static List<(string Backup, string Original)> BackupData(string backupDir)
    {
        var pairs = new List<(string, string)>();
        foreach (var name in new[] { "jobs.json", "settings.json", "secrets.json" })
        {
            var original = Path.Combine(JobStore.DataDir, name);
            try
            {
                if (!File.Exists(original)) continue;
                Directory.CreateDirectory(backupDir);
                var backup = Path.Combine(backupDir, name);
                File.Copy(original, backup, overwrite: true);
                pairs.Add((backup, original));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Không sao lưu được {name} trước khi cập nhật: {ex.Message}");
            }
        }
        return pairs;
    }

    /// <summary>%SystemRoot%\System32\cmd.exe.</summary>
    internal static string SystemCmd => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe");

    /// <summary>
    /// Script cmd: chờ tiến trình <paramref name="waitPid"/> thoát, thay file (giữ bản cũ .old, khôi phục nếu thay lỗi), mở lại, tự xóa.
    /// Có <paramref name="markerPath"/>: mở bản mới kèm biến môi trường <see cref="MarkerVariable"/>, chờ tối đa <paramref name="healthSeconds"/> giây
    /// tới khi bản mới tạo file đánh dấu (khởi động xong) mới xóa .old; bản mới thoát hoặc quá giờ → dừng bản mới, khôi phục .old, mở bản cũ và để lại
    /// file ghi chú <see cref="RollbackNoteSuffix"/> cho bản cũ báo. <paramref name="launchArgs"/>: tham số cho bản mới (chỉ dùng khi kiểm thử).
    /// </summary>
    internal static string BuildUpdateScript(string newExe, string targetExe, int waitPid, bool restart, string? markerPath = null,
        int healthSeconds = HealthWaitSeconds, string? cleanupDir = null, string newVersion = "", string launchArgs = "",
        IReadOnlyList<(string Backup, string Original)>? restoreOnRollback = null)
    {
        // Trong file .cmd "%" phải viết "%%" — đường dẫn có ký tự % vẫn đúng.
        static string Q(string path) => "\"" + path.Replace("%", "%%") + "\"";
        string target = Q(targetExe), old = Q(targetExe + ".old"), failed = Q(targetExe + ".failed");
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        // Đường dẫn đầy đủ: PATH có thể chứa find/ping của Git for Windows (Unix) đứng trước bản của Windows.
        // ping thay cho timeout: timeout báo lỗi khi không có bàn phím (cửa sổ ẩn).
        sb.AppendLine("set \"SYS=%SystemRoot%\\System32\"");
        sb.AppendLine($"set PID={waitPid}");
        sb.AppendLine(":wait");
        sb.AppendLine("\"%SYS%\\tasklist.exe\" /FI \"PID eq %PID%\" /NH 2>nul | \"%SYS%\\find.exe\" \" %PID% \" >nul && (\"%SYS%\\PING.EXE\" -n 2 127.0.0.1 >nul & goto wait)");
        // Không đổi tên được bản cũ (đang bị khóa) → không thay gì; thay lỗi → trả bản cũ về chỗ cũ. Cả hai trường hợp mở lại bản cũ.
        sb.AppendLine($"move /y {target} {old} >nul || goto keep");
        sb.AppendLine($"move /y {Q(newExe)} {target} >nul || (move /y {old} {target} >nul & goto keep)");
        if (restart && markerPath != null)
        {
            string marker = Q(markerPath), pid = Q(markerPath + ".pid"), second = Q(markerPath + SecondInstanceSuffix);
            sb.AppendLine($"set \"{MarkerVariable}={markerPath.Replace("%", "%%")}\"");
            sb.AppendLine($"del /f /q {marker} {pid} {second} 2>nul");
            sb.AppendLine($"start \"\" /b {target}{(launchArgs.Length > 0 ? " " + launchArgs : "")}");
            sb.AppendLine($"set \"{MarkerVariable}=\"");
            sb.AppendLine("set /a N=0");
            sb.AppendLine(":health");
            sb.AppendLine($"if exist {marker} goto healthy");
            // Một ScheduleApp khác (người dùng vừa mở) đã giữ khóa chạy một phiên bản: bản mới tới được Main rồi nhường → không phải lỗi.
            sb.AppendLine($"if exist {second} goto healthy");
            // File exe đang chạy thì không mở để ghi được: mở được = bản mới đã thoát (xem lại file đánh dấu phòng khi vừa ghi rồi mới thoát).
            sb.AppendLine($"2>nul (>>{target} (call )) && (if exist {marker} (goto healthy) else (set REASON=exited& goto rollback))");
            sb.AppendLine("set /a N+=1");
            sb.AppendLine($"if %N% GEQ {healthSeconds} (set REASON=timeout& goto rollback)");
            sb.AppendLine("\"%SYS%\\PING.EXE\" -n 2 127.0.0.1 >nul");
            sb.AppendLine("goto health");
            sb.AppendLine(":rollback");
            sb.AppendLine($"if exist {marker} goto healthy");
            sb.AppendLine($"if exist {second} goto healthy");
            // Bản mới còn chạy nhưng không báo khởi động xong (treo) → dừng theo PID nó tự ghi lúc mở.
            sb.AppendLine($"if \"%REASON%\"==\"timeout\" if exist {pid} for /f \"usebackq delims=\" %%p in ({pid}) do \"%SYS%\\taskkill.exe\" /PID %%p /F >nul 2>nul");
            // Treo trước khi tới Main (chưa ghi PID — vd hộp thoại "cần cài .NET") → dừng theo đúng đường dẫn file (không đụng bản cài ở chỗ khác).
            string psPath = targetExe.Replace("'", "''").Replace("%", "%%");
            string psName = Path.GetFileNameWithoutExtension(targetExe).Replace("'", "''").Replace("%", "%%");
            sb.AppendLine($"if \"%REASON%\"==\"timeout\" if not exist {pid} \"%SYS%\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -NonInteractive -Command " +
                          $"\"Get-Process -Name '{psName}' -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -eq '{psPath}' }} | Stop-Process -Force\" >nul 2>nul");
            sb.AppendLine($"if not exist {old} (start \"\" /b {target} & goto end)");
            // Đổi tên được cả khi file còn bị khóa; chờ bản mới thoát hẳn (tối đa ~10 giây) để bản cũ mở lên không gặp khóa chạy một phiên bản.
            sb.AppendLine($"move /y {target} {failed} >nul");
            sb.AppendLine($"move /y {old} {target} >nul");
            sb.AppendLine("set /a K=0");
            sb.AppendLine(":stopped");
            sb.AppendLine($"2>nul (>>{failed} (call )) && goto unlocked");
            sb.AppendLine("set /a K+=1");
            sb.AppendLine("if %K% LSS 10 (\"%SYS%\\PING.EXE\" -n 2 127.0.0.1 >nul & goto stopped)");
            sb.AppendLine(":unlocked");
            sb.AppendLine($"del /f /q {failed} 2>nul");
            foreach (var (backup, original) in restoreOnRollback ?? [])
                sb.AppendLine($"copy /y {Q(backup)} {Q(original)} >nul 2>nul");
            sb.AppendLine($">{Q(targetExe + RollbackNoteSuffix)} echo {newVersion.Replace("%", "%%")} %REASON%");
            sb.AppendLine($"start \"\" /b {target}");
            sb.AppendLine("goto end");
            sb.AppendLine(":healthy");
            sb.AppendLine($"del /f /q {old} 2>nul");
            sb.AppendLine("goto end");
        }
        else
        {
            if (restart) sb.AppendLine($"start \"\" {target}");
            sb.AppendLine("goto end");
        }
        sb.AppendLine(":keep");
        // Không thay được file (vd phần mềm diệt virus đang giữ) → vẫn mở bản cũ, để lại ghi chú để bản cũ báo cho người dùng.
        if (restart && markerPath != null) sb.AppendLine($">{Q(targetExe + RollbackNoteSuffix)} echo {newVersion.Replace("%", "%%")} locked");
        if (restart) sb.AppendLine($"start \"\" {target}");
        sb.AppendLine(":end");
        if (markerPath != null) sb.AppendLine($"del /f /q {Q(markerPath)} {Q(markerPath + ".pid")} {Q(markerPath + SecondInstanceSuffix)} 2>nul");
        if (cleanupDir != null) sb.AppendLine($"cd /d \"%TEMP%\" & rd /s /q {Q(cleanupDir)} 2>nul");
        sb.AppendLine("del \"%~f0\"");
        return sb.ToString();
    }

    /// <summary>
    /// Gọi sớm lúc khởi động: nếu do script cập nhật mở thì nhớ file đánh dấu, ghi PID (để script dừng được nếu bản mới treo)
    /// và xóa biến môi trường (chương trình flow mở ra không thừa hưởng).
    /// </summary>
    public static void BeginStartupCheck()
    {
        var marker = Environment.GetEnvironmentVariable(MarkerVariable);
        if (string.IsNullOrWhiteSpace(marker)) return;
        Environment.SetEnvironmentVariable(MarkerVariable, null);
        _healthMarker = marker;
        try { File.WriteAllText(marker + ".pid", Environment.ProcessId.ToString()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Gọi khi không phải phiên bản đầu tiên (đã có ScheduleApp khác đang chạy): nếu do script cập nhật mở thì báo cho script biết
    /// bản mới đã chạy được tới đây nhưng nhường cho phiên bản đang mở — không quay về bản cũ.
    /// </summary>
    public static void NoteSecondInstance()
    {
        var marker = Environment.GetEnvironmentVariable(MarkerVariable);
        if (string.IsNullOrWhiteSpace(marker)) return;
        try { File.WriteAllText(marker + SecondInstanceSuffix, Environment.ProcessId.ToString()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Gọi khi đã khởi động xong (dữ liệu đã nạp, giao diện đã chạy): mở bởi script cập nhật → tạo file đánh dấu để script giữ bản mới
    /// và xóa .old; lần mở thường → dọn bản cũ còn sót (script bị ngắt giữa chừng, vd tắt máy).
    /// </summary>
    public static void ConfirmStarted()
    {
        if (Interlocked.Exchange(ref _healthMarker, null) is { } marker)
        {
            try
            {
                File.WriteAllText(marker, Environment.ProcessId.ToString());
                Log.Info($"Đã khởi động bản {Current} sau khi cập nhật.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Không báo được cho script cập nhật là đã khởi động xong: " + ex.Message);
            }
            return;
        }
        CleanupOldVersion();
    }

    /// <summary>
    /// Lần cập nhật trước không thành công → câu thông báo (một lần, xóa file ghi chú); không có thì null.
    /// Bản mới không khởi động được thì bỏ qua bản đó (không mời cập nhật lại đúng bản vừa hỏng).
    /// </summary>
    public static string? TakeRollbackNote()
    {
        if (Environment.ProcessPath is not { } exe || TakeRollbackNote(exe, out var failed) is not { } note) return null;
        if (failed != null)
        {
            SettingsStore.Current.Update.SkippedVersion = failed;
            SettingsStore.Save();
        }
        return note;
    }

    internal static string? TakeRollbackNote(string exe) => TakeRollbackNote(exe, out _);

    /// <param name="failedVersion">Bản mới không khởi động được (null nếu chỉ là không thay được file).</param>
    internal static string? TakeRollbackNote(string exe, out string? failedVersion)
    {
        failedVersion = null;
        var path = exe + RollbackNoteSuffix;
        try
        {
            if (!File.Exists(path)) return null;
            var parts = File.ReadAllText(path).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            File.Delete(path);
            string version = parts.Length > 1 ? parts[0] + " " : "";
            string kind = parts.LastOrDefault() ?? "";
            if (kind == "locked")
                return $"Không thay được file ScheduleApp.exe để cập nhật lên bản {version}(file đang bị chương trình khác giữ, vd phần mềm diệt virus) — " +
                       $"vẫn dùng bản {Current}. Hãy thử cập nhật lại sau.";
            if (parts.Length > 1) failedVersion = parts[0];
            string reason = kind == "timeout"
                ? $"không báo khởi động xong sau {HealthWaitSeconds} giây"
                : "đã thoát ngay khi mở";
            return $"Bản mới {version}không khởi động được ({reason}) — đã quay về bản cũ {Current} và bỏ qua bản này. Chi tiết lỗi (nếu có) nằm trong thư mục log.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Xóa bản cũ / bản hỏng còn sót lại sau lần cập nhật trước.</summary>
    public static void CleanupOldVersion()
    {
        if (Environment.ProcessPath is { } exe) CleanupOldVersion(exe);
    }

    internal static void CleanupOldVersion(string exe)
    {
        foreach (var leftover in new[] { exe + ".old", exe + ".failed" })
        {
            try
            {
                if (File.Exists(leftover)) File.Delete(leftover);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
