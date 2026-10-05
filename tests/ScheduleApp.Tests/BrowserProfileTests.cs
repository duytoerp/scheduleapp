using System.Text.Json.Nodes;
using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;

namespace ScheduleApp.Tests;

public class BrowserProfileTests
{
    [Fact]
    public void PickTabSkipsInternalPages()
    {
        // Thứ tự thật Edge trả về: hộp thoại đồng bộ ẩn đứng trước tab đang mở.
        var tabs = new List<(string, string)>
        {
            ("edge://sync-confirmation-dialog/", "We are now syncing"),
            ("chrome-extension://abc/bg.html", "Microsoft Voices"),
            ("https://org.crm5.dynamics.com/main.aspx", "Dynamics 365"),
            ("https://login.microsoftonline.com/", "Đăng nhập")
        };
        Assert.Equal(2, BrowserClient.PickTab(tabs, ""));
        Assert.Equal(3, BrowserClient.PickTab(tabs, "login"));
        Assert.Equal(3, BrowserClient.PickTab(tabs, "đăng nhập"));   // theo tiêu đề
        Assert.Equal(0, BrowserClient.PickTab(tabs, "sync"));        // vẫn chọn được khi chỉ định rõ
        Assert.Equal(-1, BrowserClient.PickTab(tabs, "khongco"));
        Assert.Equal(-1, BrowserClient.PickTab([], ""));

        // Trình duyệt vừa mở chưa có URL: tab mới (trang nội bộ) vẫn được chọn.
        Assert.Equal(1, BrowserClient.PickTab([("edge://sync-confirmation-dialog/", ""), ("edge://newtab/", "New tab")], ""));
    }

    [Fact]
    public void ProfileDirectories()
    {
        Assert.Equal("browser-edge", Path.GetFileName(BrowserProfiles.Dir("Edge", "")));
        Assert.Equal("browser-chrome", Path.GetFileName(BrowserProfiles.Dir("", "  ")));
        Assert.Equal("browser-chrome-Kế toán", Path.GetFileName(BrowserProfiles.Dir("Chrome", "Kế toán")));
        Assert.Equal("A_B_C", BrowserProfiles.SafeName(" A/B:C. "));
        Assert.Equal("Edge", BrowserProfiles.DisplayName("microsoft edge"));
    }

    private static string FakeUserData()
    {
        var udd = Path.Combine(TestSupport.NewDir(), "User Data");
        void Put(string rel, string content)
        {
            var path = Path.Combine(udd, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        Put("Local State", """
            {"os_crypt":{"encrypted_key":"KHOA"},
             "profile":{"info_cache":{"Default":{"name":"Công ty","user_name":"a@cty.vn"},"Profile 2":{"name":"Tai","user_name":"t@gmail.com"},
                        "Profile 9":{"name":"Đã xóa"}},
                        "last_used":"Profile 2","profiles_order":["Default","Profile 2"]}}
            """);
        Put(@"Default\Preferences", "{}");
        Put(@"Profile 2\Preferences", "{\"tai\":1}");
        Put(@"Profile 2\Network\Cookies", "cookie");
        Put(@"Profile 2\Cache\Cache_Data\data_0", new string('x', 1000));
        Put(@"Profile 2\Service Worker\CacheStorage\a", "x");
        Put(@"Profile 2\Service Worker\Database\b", "giữ");
        Put(@"Profile 2\Sessions\Session_1", "tab cũ");
        Put(@"Profile 2\Current Session", "tab cũ");
        Put(@"Profile 2\Extensions\abc\1.0\manifest.json", "{}");
        return udd;
    }

    [Fact]
    public void CopiesRealProfileWithoutCachesAndSessions()
    {
        var udd = FakeUserData();
        var profiles = BrowserProfiles.RealProfiles("Chrome", udd);
        Assert.Equal(new[] { "Default", "Profile 2" }, profiles.Select(p => p.Directory));   // "Profile 9" không có thư mục → bỏ
        var tai = profiles[1];
        Assert.Equal("Tai", tai.Name);
        Assert.Contains("t@gmail.com", tai.ToString());

        var result = BrowserProfiles.CopyFromReal(tai, "Tai");
        Assert.Equal("Tai", result.Profile);
        var dir = BrowserProfiles.Dir("Chrome", "Tai");
        Assert.Equal("{\"tai\":1}", File.ReadAllText(Path.Combine(dir, @"Default\Preferences")));
        Assert.True(File.Exists(Path.Combine(dir, @"Default\Network\Cookies")));
        Assert.True(File.Exists(Path.Combine(dir, @"Default\Extensions\abc\1.0\manifest.json")));
        Assert.True(File.Exists(Path.Combine(dir, @"Default\Service Worker\Database\b")));
        Assert.False(Directory.Exists(Path.Combine(dir, @"Default\Cache")));
        Assert.False(Directory.Exists(Path.Combine(dir, @"Default\Service Worker\CacheStorage")));
        Assert.False(Directory.Exists(Path.Combine(dir, @"Default\Sessions")));
        Assert.False(File.Exists(Path.Combine(dir, @"Default\Current Session")));

        var state = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "Local State")))!;
        Assert.Equal("KHOA", (string?)state["os_crypt"]!["encrypted_key"]);     // khóa giải mã cookie đi kèm
        var cache = state["profile"]!["info_cache"]!.AsObject();
        Assert.Equal(new[] { "Default" }, cache.Select(kv => kv.Key));
        Assert.Equal("Tai", (string?)cache["Default"]!["name"]);
        Assert.Equal("Default", (string?)state["profile"]!["last_used"]);
        Assert.Equal("[\"Default\"]", state["profile"]!["profiles_order"]!.ToJsonString());

        Assert.Contains("Tai", BrowserProfiles.List("Chrome"));
        Assert.DoesNotContain("Tai", BrowserProfiles.List("Edge"));
    }

    [Fact]
    public void LockedSourceKeepsPreviousCopy()
    {
        var udd = FakeUserData();
        var tai = BrowserProfiles.RealProfiles("Chrome", udd).Single(p => p.Directory == "Profile 2");
        BrowserProfiles.CopyFromReal(tai, "Tai khoa");
        var dir = BrowserProfiles.Dir("Chrome", "Tai khoa");
        File.WriteAllText(Path.Combine(dir, "danh-dau.txt"), "bản cũ");

        // Trình duyệt đang mở hồ sơ → file cookie bị khóa độc quyền.
        using (new FileStream(Path.Combine(udd, @"Profile 2\Network\Cookies"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => BrowserProfiles.CopyFromReal(tai, "Tai khoa"));
            Assert.Contains("đóng hết cửa sổ Chrome", ex.Message);
            Assert.Contains(@"Network\Cookies", ex.Message);
        }
        Assert.Equal("bản cũ", File.ReadAllText(Path.Combine(dir, "danh-dau.txt")));
        Assert.Empty(Directory.GetDirectories(BrowserProfiles.Root, "*.tmp-*"));
        Assert.False(BrowserProfiles.InUse(dir));
    }

    /// <summary>
    /// Trình duyệt thật (chạy ẩn): mỗi hồ sơ giữ cookie riêng, đổi hồ sơ thì đóng trình duyệt cũ,
    /// và hồ sơ sao chép vẫn còn đăng nhập (cookie giải mã được bằng khóa trong "Local State" đi kèm).
    /// </summary>
    [LiveFact]
    public async Task ProfilesKeepCookiesAndCopiesStayLoggedIn()
    {
        var browser = new[]
        {
            (@"%ProgramFiles%\Google\Chrome\Application\chrome.exe", "Chrome"),
            (@"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe", "Chrome"),
            (@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe", "Edge")
        }.FirstOrDefault(b => File.Exists(Environment.ExpandEnvironmentVariables(b.Item1))).Item2;
        Assert.True(browser != null, "Máy không có Chrome / Edge.");

        using var server = new MiniHttpServer((_, _, _, _) => (200, "{}"));
        var host = new Uri(server.BaseUrl).Authority;
        SettingsStore.Current.BrowserPort = 9335;
        BrowserClient.ExtraLaunchArgs = " --headless=new";
        var job = new Job { Name = "ho-so" };
        var ctx = new FlowContext(job, new FakeUi(), RunOptions.Default, _ => null, CancellationToken.None);
        async Task<string> Do(BrowserAction action, string text, string args = "", string target = "")
        {
            var s = ActionStep.CreateDefault(StepType.Browser);
            s.BrowserAction = action; s.Text = text; s.Arguments = args; s.Target = target; s.Variable = "kq"; s.DelayMs = 10_000;
            ctx.Vars["kq"] = "";
            await StepExecutor.ExecuteAsync(ctx.ExpandStep(s), job, ctx);
            return ctx.Vars["kq"];
        }
        Task Open(string profile) => Do(BrowserAction.Launch, server.BaseUrl, profile, browser!);
        Task<string> Cookie() => Do(BrowserAction.RunScript, "document.cookie", target: host);
        string Dir(string p) => BrowserProfiles.Dir(browser!, p);

        try
        {
            await Open("Gốc");
            Assert.True(BrowserProfiles.InUse(Dir("Gốc")));
            await Do(BrowserAction.RunScript, "document.cookie = 'phien=abc123; max-age=86400; path=/'; document.cookie", target: host);
            Assert.Equal("phien=abc123", await Cookie());

            // Đang mở → sao chép báo lỗi, không để lại gì.
            var source = new BrowserProfiles.RealProfile(BrowserProfiles.Key(browser!), Dir("Gốc"), "Default", "Gốc", "");
            Assert.Throws<IOException>(() => BrowserProfiles.CopyFromReal(source, "Bản sao"));
            Assert.False(Directory.Exists(Dir("Bản sao")));

            await Open("Khác");   // đóng hồ sơ "Gốc", mở hồ sơ "Khác"
            Assert.False(BrowserProfiles.InUse(Dir("Gốc")));
            Assert.True(BrowserProfiles.InUse(Dir("Khác")));
            Assert.Equal("", await Cookie());

            await Open("Khác");   // cùng hồ sơ → chỉ mở thêm tab
            Assert.True(BrowserProfiles.InUse(Dir("Khác")));

            var copy = BrowserProfiles.CopyFromReal(source, "Bản sao");
            Assert.True(copy.Files > 0);
            await Open("Bản sao");
            Assert.Equal("phien=abc123", await Cookie());

            await Open("Gốc");
            Assert.Equal("phien=abc123", await Cookie());
        }
        finally
        {
            BrowserClient.ExtraLaunchArgs = "";
            foreach (var d in BrowserProfiles.AllDirs().Where(BrowserProfiles.InUse).ToList())
            {
                try { await BrowserClient.CloseBrowserAsync(d, CancellationToken.None); }
                catch (TimeoutException) { }
            }
            // Trình duyệt còn chạy sẽ giữ output của tiến trình test → dotnet test không kết thúc.
            try { BrowserClient.LastLaunched?.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* đã thoát */ }
        }
    }
}
