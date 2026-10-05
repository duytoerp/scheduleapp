using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using ScheduleApp.Services;

namespace ScheduleApp.Automation;

/// <summary>
/// Hồ sơ trình duyệt của ScheduleApp: mỗi hồ sơ là một thư mục dữ liệu riêng (đăng nhập, cookie, tiện ích giữ nguyên giữa các lần chạy),
/// vd "browser-chrome" (mặc định), "browser-chrome-Kế toán".
/// Chrome/Edge chỉ cho điều khiển qua cổng remote debugging khi dùng thư mục dữ liệu khác thư mục mặc định, nên hồ sơ thật
/// của người dùng (vd hồ sơ "Tai" của Chrome) được dùng bằng cách sao chép một lần sang hồ sơ của ScheduleApp.
/// Hồ sơ chứa cookie và khóa giải mã đăng nhập ("Local State") nên nằm ở <see cref="Root"/> (máy này, chỉ tài khoản Windows hiện tại đọc được),
/// không nằm trong %AppData% (Roaming) — thư mục có thể được đồng bộ lên máy chủ của công ty.
/// </summary>
internal static class BrowserProfiles
{
    /// <summary>
    /// Thư mục gốc chứa các hồ sơ: %LocalAppData%\ScheduleApp\BrowserProfiles. Bản portable / kiểm thử (SCHEDULEAPP_DATA_DIR)
    /// → "BrowserProfiles" trong thư mục dữ liệu đó.
    /// </summary>
    public static string Root { get; } = DefaultRoot();

    /// <summary>Nơi các phiên bản trước lưu hồ sơ (thư mục dữ liệu, mặc định %AppData%\ScheduleApp — Roaming).</summary>
    private static string LegacyRoot => JobStore.DataDir;

    private static string DefaultRoot()
    {
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScheduleApp");
        var baseDir = string.Equals(Path.GetFullPath(JobStore.DataDir).TrimEnd('\\'), Path.GetFullPath(roaming).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScheduleApp")
            : JobStore.DataDir;
        return Path.Combine(baseDir, "BrowserProfiles");
    }

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

    /// <summary>Thư mục dữ liệu của hồ sơ (trống = hồ sơ mặc định của ScheduleApp). Có thể chuyển hồ sơ cũ (chép qua ổ mạng) — không gọi trên luồng giao diện.</summary>
    public static string Dir(string browser, string profile) => Locate(DirName(browser, profile), Root, LegacyRoot);

    /// <summary>Hồ sơ đã có (ở chỗ mới hoặc chỗ cũ chưa chuyển) — chỉ kiểm tra, không chuyển hồ sơ, gọi trên luồng giao diện được.</summary>
    public static bool Exists(string browser, string profile) => Exists(DirName(browser, profile), Root, LegacyRoot);

    internal static bool Exists(string name, string root, string legacyRoot) =>
        System.IO.Directory.Exists(Path.Combine(root, name)) || System.IO.Directory.Exists(Path.Combine(legacyRoot, name));

    private static string DirName(string browser, string profile)
    {
        var name = SafeName(profile);
        return "browser-" + Key(browser) + (name.Length == 0 ? "" : "-" + name);
    }

    /// <summary>Hồ sơ cũ chuyển bị lỗi trong phiên này — không chép lại ở mỗi lần dùng (chép qua ổ mạng có thể mất vài phút); mở lại ứng dụng thì thử lại.</summary>
    private static readonly HashSet<string> FailedMoves = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Thư mục hồ sơ <paramref name="name"/> trong <paramref name="root"/>; bản cũ còn ở <paramref name="legacyRoot"/> được chuyển sang ở lần dùng đầu tiên.
    /// Trình duyệt đang mở hồ sơ đó → tạm dùng chỗ cũ, lần sau thử lại; lỗi ổ đĩa → dùng chỗ cũ tới hết phiên.
    /// </summary>
    internal static string Locate(string name, string root, string legacyRoot)
    {
        EnsureRoot(root);
        var target = Path.Combine(root, name);
        var legacy = Path.Combine(legacyRoot, name);
        if (System.IO.Directory.Exists(target) || !System.IO.Directory.Exists(legacy)) return target;
        lock (FailedMoves)
        {
            if (FailedMoves.Contains(legacy)) return legacy;
        }
        if (InUse(legacy)) return legacy;
        try
        {
            MoveProfile(legacy, target);
            Log.Info($"Đã chuyển hồ sơ trình duyệt \"{name}\" sang {root} (chỉ tài khoản Windows này đọc được).");
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (FailedMoves) FailedMoves.Add(legacy);
            Log.Warn($"Chưa chuyển được hồ sơ trình duyệt \"{name}\" sang {root}: {ex.Message} — tạm dùng chỗ cũ, lần mở ScheduleApp sau thử lại.");
            return System.IO.Directory.Exists(target) ? target : legacy;
        }
    }

    /// <summary>Chuyển thư mục hồ sơ: cùng ổ đĩa → đổi chỗ; khác ổ (vd %AppData% chuyển hướng lên ổ mạng) → chép rồi xóa bản cũ.</summary>
    internal static void MoveProfile(string source, string target)
    {
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase))
            System.IO.Directory.Move(source, target);
        else
            CopyThenDelete(source, target);
        RestrictToCurrentUser(target);
    }

    internal static void CopyThenDelete(string source, string target)
    {
        var tmp = target + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            CopyAll(source, tmp);
            System.IO.Directory.Move(tmp, target);
        }
        catch
        {
            try { if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* để lại thư mục tạm, lần sau bỏ qua */ }
            throw;
        }
        try { System.IO.Directory.Delete(source, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Đã chép hồ sơ trình duyệt sang {target} nhưng chưa xóa được bản cũ {source}: {ex.Message}");
        }
    }

    private static void CopyAll(string src, string dst)
    {
        System.IO.Directory.CreateDirectory(dst);
        foreach (var file in System.IO.Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)));
        foreach (var dir in System.IO.Directory.GetDirectories(src))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            CopyAll(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }
    }

    private static readonly HashSet<string> PreparedRoots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tạo thư mục gốc (một lần mỗi phiên) và giới hạn quyền chỉ cho tài khoản Windows hiện tại.</summary>
    internal static void EnsureRoot(string root)
    {
        lock (PreparedRoots)
        {
            if (PreparedRoots.Contains(root) && System.IO.Directory.Exists(root)) return;
            try
            {
                if (!System.IO.Directory.Exists(root))
                {
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(root))!);
                    new DirectoryInfo(root).Create(CurrentUserOnly());
                }
                else if (!IsCurrentUserOnly(root)) RestrictToCurrentUser(root);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
            {
                // Ổ đĩa không hỗ trợ phân quyền (vd FAT32, ổ mạng) → vẫn dùng được, chỉ báo lại.
                System.IO.Directory.CreateDirectory(root);
                Log.Warn($"Không giới hạn được quyền thư mục hồ sơ trình duyệt {root}: {ex.Message}");
            }
            PreparedRoots.Add(root);
        }
    }

    /// <summary>Quyền chỉ cho tài khoản hiện tại, không kế thừa từ thư mục cha (vd nhóm Users / Administrators); thư mục con, file kế thừa quyền này.</summary>
    private static DirectorySecurity CurrentUserOnly()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    internal static void RestrictToCurrentUser(string dir) => new DirectoryInfo(dir).SetAccessControl(CurrentUserOnly());

    /// <summary>Thư mục không kế thừa quyền và chỉ tài khoản hiện tại được cấp quyền.</summary>
    internal static bool IsCurrentUserOnly(string dir)
    {
        var security = new DirectoryInfo(dir).GetAccessControl();
        var user = WindowsIdentity.GetCurrent().User!;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        return security.AreAccessRulesProtected && rules.Count > 0 &&
               rules.All(r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(user));
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
        return [.. AllDirs()
            .Select(Path.GetFileName)
            .Where(n => n != null && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(n => n![prefix.Length..])
            .Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Mọi thư mục hồ sơ của ScheduleApp (mọi trình duyệt), kể cả hồ sơ còn ở chỗ cũ chưa chuyển được.</summary>
    public static IEnumerable<string> AllDirs()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { LegacyRoot, Root }) // cùng tên → bản ở chỗ mới
        {
            if (!System.IO.Directory.Exists(root)) continue;
            foreach (var dir in System.IO.Directory.GetDirectories(root, "browser-*").Where(d => !Path.GetFileName(d).Contains(".tmp-")))
                found[Path.GetFileName(dir)] = dir;
        }
        return found.Values;
    }

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
