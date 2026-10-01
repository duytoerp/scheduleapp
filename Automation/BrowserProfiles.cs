using System.Text.Json.Nodes;
using ScheduleApp.Services;

namespace ScheduleApp.Automation;

/// <summary>
/// Hồ sơ trình duyệt của ScheduleApp: mỗi hồ sơ là một thư mục dữ liệu riêng (đăng nhập, cookie, tiện ích giữ nguyên giữa các lần chạy),
/// vd "browser-chrome" (mặc định), "browser-chrome-Kế toán".
/// Chrome/Edge chỉ cho điều khiển qua cổng remote debugging khi dùng thư mục dữ liệu khác thư mục mặc định, nên hồ sơ thật
/// của người dùng (vd hồ sơ "Tai" của Chrome) được dùng bằng cách sao chép một lần sang hồ sơ của ScheduleApp.
/// </summary>
internal static class BrowserProfiles
{
    /// <summary>Khóa trình duyệt dùng trong tên thư mục: "chrome", "edge" hoặc tên file exe khi nhập đường dẫn.</summary>
    public static string Key(string browser)
    {
        var b = browser.Trim().Trim('"');
        if (b.Length > 0 && File.Exists(b)) return Path.GetFileNameWithoutExtension(b).ToLowerInvariant();
        return b.Contains("edge", StringComparison.OrdinalIgnoreCase) ? "edge" : "chrome";
    }

    public static string DisplayName(string browser) => Key(browser) switch
    {
        "edge" or "msedge" => "Edge",
        "chrome" => "Chrome",
        var k => k
    };

    /// <summary>Thư mục dữ liệu của hồ sơ (trống = hồ sơ mặc định của ScheduleApp).</summary>
    public static string Dir(string browser, string profile)
    {
        var name = SafeName(profile);
        var dir = "browser-" + Key(browser) + (name.Length == 0 ? "" : "-" + name);
        return Path.Combine(JobStore.DataDir, dir);
    }

    public static string SafeName(string profile)
    {
        var chars = profile.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim().Trim('.');
    }

    /// <summary>Tên các hồ sơ đã tạo của trình duyệt (không gồm hồ sơ mặc định).</summary>
    public static List<string> List(string browser)
    {
        var prefix = "browser-" + Key(browser) + "-";
        if (!Directory.Exists(JobStore.DataDir)) return [];
        return [.. Directory.GetDirectories(JobStore.DataDir, prefix + "*")
            .Select(Path.GetFileName)
            .Where(n => n != null && !n.Contains(".tmp-"))
            .Select(n => n![prefix.Length..])
            .Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Mọi thư mục hồ sơ của ScheduleApp (mọi trình duyệt).</summary>
    public static IEnumerable<string> AllDirs() =>
        Directory.Exists(JobStore.DataDir)
            ? Directory.GetDirectories(JobStore.DataDir, "browser-*").Where(d => !d.Contains(".tmp-"))
            : [];

    /// <summary>
    /// Trình duyệt đang mở thư mục dữ liệu này: Chrome/Edge giữ file "lockfile" (mở kèm quyền xóa, tự xóa khi thoát) suốt lúc chạy.
    /// Mở lại mà không cho phép xóa sẽ bị từ chối khi file đang được giữ; file sót lại sau khi trình duyệt bị tắt đột ngột thì mở được.
    /// </summary>
    public static bool InUse(string dir)
    {
        var lockFile = Path.Combine(dir, "lockfile");
        if (!File.Exists(lockFile)) return false;
        try
        {
            using var _ = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    // ───────────────────────────── Hồ sơ thật của Chrome / Edge ─────────────────────────────

    /// <param name="Directory">Thư mục con trong User Data, vd "Default", "Profile 2".</param>
    public sealed record RealProfile(string Browser, string UserDataDir, string Directory, string Name, string Account)
    {
        public override string ToString() => Name + (Account.Length > 0 ? $" — {Account}" : "") + $"  ({Directory})";
    }

    public static string? RealUserDataDir(string browser)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Key(browser) switch
        {
            "edge" or "msedge" => Path.Combine(local, "Microsoft", "Edge", "User Data"),
            "chrome" => Path.Combine(local, "Google", "Chrome", "User Data"),
            _ => null
        };
    }

    /// <summary>Các hồ sơ người dùng đang có trong Chrome / Edge (đọc từ "Local State").</summary>
    public static List<RealProfile> RealProfiles(string browser, string? userDataDir = null)
    {
        userDataDir ??= RealUserDataDir(browser);
        var list = new List<RealProfile>();
        if (userDataDir == null) return list;
        var localState = Path.Combine(userDataDir, "Local State");
        if (!File.Exists(localState)) return list;
        try
        {
            if (JsonNode.Parse(ReadShared(localState))?["profile"]?["info_cache"] is not JsonObject cache) return list;
            foreach (var (dir, info) in cache)
            {
                if (!System.IO.Directory.Exists(Path.Combine(userDataDir, dir))) continue;
                var name = info?["name"]?.GetValue<string>() ?? dir;
                var account = info?["user_name"]?.GetValue<string>() ?? "";
                list.Add(new RealProfile(Key(browser), userDataDir, dir, name, account));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Log.Warn($"Không đọc được danh sách hồ sơ {DisplayName(browser)}: {ex.Message}");
        }
        return [.. list.OrderBy(p => p.Directory == "Default" ? 0 : 1).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public sealed record CopyResult(string Profile, long Bytes, int Files);

    /// <summary>
    /// Thư mục/file không cần sao chép: bộ nhớ đệm (tự tạo lại, chiếm phần lớn dung lượng) và phiên đang mở
    /// (để trình duyệt điều khiển không tự mở lại các tab của người dùng).
    /// </summary>
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GrShaderCache", "GraphiteDawnCache",
        "ShaderCache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache", "blob_storage", "Download Service",
        "optimization_guide_hint_cache_store", "optimization_guide_model_and_features_store", "optimization_guide_prediction_model_downloads",
        "Safe Browsing Network", "VideoDecodeStats", "Crashpad", "JumpListIconsMostVisited", "JumpListIconsRecentClosed", "Sessions"
    };

    private static readonly HashSet<string> SkipFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Current Session", "Current Tabs", "Last Session", "Last Tabs"
    };

    /// <summary>
    /// Sao chép hồ sơ thật sang hồ sơ <paramref name="profileName"/> của ScheduleApp (ghi đè nếu đã có). Không thay đổi hồ sơ gốc.
    /// Trình duyệt phải đóng hồ sơ nguồn (file cookie / đăng nhập bị khóa khi đang mở).
    /// </summary>
    public static CopyResult CopyFromReal(RealProfile source, string profileName, CancellationToken ct = default)
    {
        var name = SafeName(profileName);
        if (name.Length == 0) throw new ArgumentException("Hãy đặt tên cho hồ sơ.");
        var target = Dir(source.Browser, name);
        var browserName = DisplayName(source.Browser);
        if (InUse(target))
            throw new IOException($"Hồ sơ \"{name}\" của ScheduleApp đang mở — hãy đóng cửa sổ {browserName} đó rồi thử lại.");

        var src = Path.Combine(source.UserDataDir, source.Directory);
        if (!System.IO.Directory.Exists(src)) throw new DirectoryNotFoundException($"Không thấy thư mục hồ sơ {src}.");

        var tmp = target + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        var state = new CopyState();
        try
        {
            CopyTree(src, Path.Combine(tmp, "Default"), "", state, ct);
            if (state.Locked.Count > 0)
                throw new IOException(
                    $"{browserName} đang mở hồ sơ \"{source.Name}\" nên không chép được {state.Locked.Count} file (vd \"{state.Locked[0]}\"). " +
                    $"Hãy đóng hết cửa sổ {browserName} dùng hồ sơ này — kể cả biểu tượng {browserName} ở khay hệ thống — rồi thử lại.");
            WriteLocalState(source, Path.Combine(tmp, "Local State"));

            if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, recursive: true);
            System.IO.Directory.Move(tmp, target);
            return new CopyResult(name, state.Bytes, state.Files);
        }
        catch
        {
            try { if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* để lại thư mục tạm, lần sau bỏ qua */ }
            throw;
        }
    }

    private sealed class CopyState
    {
        public long Bytes;
        public int Files;
        public readonly List<string> Locked = [];
    }

    private static void CopyTree(string src, string dst, string relative, CopyState state, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(dst);
        foreach (var file in System.IO.Directory.GetFiles(src))
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            if (relative.Length == 0 && SkipFiles.Contains(fileName)) continue;
            try
            {
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(Path.Combine(dst, fileName), FileMode.Create, FileAccess.Write))
                {
                    input.CopyTo(output);
                    state.Bytes += output.Length;
                }
                File.SetLastWriteTimeUtc(Path.Combine(dst, fileName), File.GetLastWriteTimeUtc(file));
                state.Files++;
            }
            catch (IOException) when (!ct.IsCancellationRequested)
            {
                state.Locked.Add(Path.Combine(relative, fileName));
            }
        }
        foreach (var dir in System.IO.Directory.GetDirectories(src))
        {
            var rel = Path.Combine(relative, Path.GetFileName(dir));
            if (SkipDirs.Contains(rel)) continue;
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            CopyTree(dir, Path.Combine(dst, Path.GetFileName(dir)), rel, state, ct);
        }
    }

    /// <summary>
    /// "Local State" chứa khóa giải mã cookie / mật khẩu của hồ sơ (os_crypt) — phải đi kèm hồ sơ.
    /// Danh sách hồ sơ được rút còn đúng hồ sơ vừa chép (đổi thành "Default") để trình duyệt mở thẳng vào nó.
    /// </summary>
    private static void WriteLocalState(RealProfile source, string path)
    {
        var src = Path.Combine(source.UserDataDir, "Local State");
        var root = JsonNode.Parse(ReadShared(src)) as JsonObject ?? new JsonObject();
        if (root["profile"] is JsonObject profile)
        {
            var entry = (profile["info_cache"] as JsonObject)?[source.Directory]?.DeepClone() ?? new JsonObject();
            profile["info_cache"] = new JsonObject { ["Default"] = entry };
            profile["last_used"] = "Default";
            profile["last_active_profiles"] = new JsonArray("Default");
            if (profile.ContainsKey("profiles_order")) profile["profiles_order"] = new JsonArray("Default");
        }
        File.WriteAllText(path, root.ToJsonString());
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
