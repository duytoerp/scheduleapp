using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace ScheduleApp.Services.Testing;

/// <summary>
/// Xuất báo cáo kiểm thử: index.html (xem trên trình duyệt, kèm ảnh lúc lỗi) và junit.xml (Azure DevOps / Jenkins / GitHub Actions đọc được).
/// </summary>
public static class TestReport
{
    /// <summary>Thư mục chứa các báo cáo kiểm thử.</summary>
    public static string RootDir => Path.Combine(JobStore.DataDir, "test-reports");

    /// <summary>Tạo thư mục báo cáo mới: &lt;gốc&gt;\yyyy-MM-dd_HHmmss_Tên.</summary>
    public static string NewFolder(string name, string? root = null)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c)).Trim('_');
        if (safe.Length > 50) safe = safe[..50];
        var baseDir = Path.Combine(root ?? RootDir, $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{(safe.Length == 0 ? "test" : safe)}");
        var dir = baseDir;
        for (int i = 2; Directory.Exists(dir); i++) dir = $"{baseDir}-{i}";
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Ghi index.html + junit.xml vào <paramref name="folder"/>; trả về đường dẫn index.html.</summary>
    /// <param name="environment">Môi trường kiểm thử (hiện trong báo cáo), null = không dùng môi trường.</param>
    public static string Write(string folder, string suiteName, IReadOnlyList<TestCaseResult> cases, string? environment = null)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "junit.xml"), JUnit(suiteName, cases, environment), new UTF8Encoding(false));
        var html = Path.Combine(folder, "index.html");
        File.WriteAllText(html, Html(suiteName, cases, folder, environment), new UTF8Encoding(false));
        RememberLatest(cases.Select(c => c.JobId), html);
        return html;
    }

    // ───────────────────────────── Báo cáo gần đây ─────────────────────────────

    private static readonly object LatestSync = new();
    private static string LatestFile => Path.Combine(RootDir, "latest.json");

    /// <summary>Báo cáo mới nhất có kịch bản <paramref name="jobId"/> (null nếu chưa có hoặc đã bị xóa).</summary>
    public static string? LatestFor(Guid jobId)
    {
        lock (LatestSync)
        {
            var map = ReadLatest();
            return map.TryGetValue(jobId.ToString(), out var path) && File.Exists(path) ? path : null;
        }
    }

    private static void RememberLatest(IEnumerable<Guid> jobIds, string report)
    {
        lock (LatestSync)
        {
            try
            {
                var map = ReadLatest();
                foreach (var id in jobIds.Where(id => id != Guid.Empty)) map[id.ToString()] = report;
                Directory.CreateDirectory(RootDir);
                File.WriteAllText(LatestFile, System.Text.Json.JsonSerializer.Serialize(map));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Dictionary<string, string> ReadLatest()
    {
        try
        {
            if (File.Exists(LatestFile))
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LatestFile)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        return [];
    }

    /// <summary>Một báo cáo đã xuất: thời điểm, tên bộ, số kịch bản / số không đạt.</summary>
    public sealed record ReportInfo(string Path, DateTime Time, string Name, int Tests, int Failures);

    /// <summary>Các báo cáo gần đây nhất (đọc từ junit.xml của từng thư mục).</summary>
    public static List<ReportInfo> Recent(int max = 30)
    {
        var list = new List<ReportInfo>();
        if (!Directory.Exists(RootDir)) return list;
        foreach (var dir in Directory.GetDirectories(RootDir).OrderByDescending(d => System.IO.Path.GetFileName(d), StringComparer.Ordinal).Take(max))
        {
            var junit = System.IO.Path.Combine(dir, "junit.xml");
            var html = System.IO.Path.Combine(dir, "index.html");
            if (!File.Exists(junit) || !File.Exists(html)) continue;
            try
            {
                var root = XDocument.Load(junit).Root!;
                int Attr(string n) => int.TryParse(root.Attribute(n)?.Value, out var v) ? v : 0;
                var name = System.IO.Path.GetFileName(dir);
                var time = DateTime.TryParseExact(name.Length >= 17 ? name[..17] : "", "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var t) ? t : Directory.GetCreationTime(dir);
                list.Add(new ReportInfo(html, time, root.Attribute("name")?.Value ?? name, Attr("tests"), Attr("failures")));
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>Một dòng tóm tắt, vd "3/4 kịch bản đạt · 12/13 kiểm tra đạt · 1 phút 05 giây".</summary>
    public static string Summary(IReadOnlyList<TestCaseResult> cases)
    {
        int passed = cases.Count(c => c.Ok), flaky = cases.Count(c => c.Flaky);
        int ap = cases.Sum(c => c.AssertsPassed), af = cases.Sum(c => c.AssertsFailed);
        return $"{passed}/{cases.Count} kịch bản đạt" + (flaky > 0 ? $" ({flaky} đạt sau khi chạy lại)" : "") +
               $" · {ap}/{ap + af} kiểm tra đạt · {Duration(cases.Sum(c => c.Seconds))}";
    }

    // ───────────────────────────── JUnit XML ─────────────────────────────

    internal static string JUnit(string suiteName, IReadOnlyList<TestCaseResult> cases, string? environment = null)
    {
        static string Sec(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);
        static XElement Prop(string name, string value) => new("property", new XAttribute("name", name), new XAttribute("value", value));

        var suites = new XElement("testsuites",
            new XAttribute("name", suiteName),
            new XAttribute("tests", cases.Count),
            new XAttribute("failures", cases.Count(c => !c.Ok)),
            new XAttribute("time", Sec(cases.Sum(c => c.Seconds))));

        foreach (var group in cases.GroupBy(c => c.Group.Length == 0 ? suiteName : c.Group))
        {
            var suite = new XElement("testsuite",
                new XAttribute("name", group.Key),
                new XAttribute("tests", group.Count()),
                new XAttribute("failures", group.Count(c => !c.Ok)),
                new XAttribute("errors", 0),
                new XAttribute("time", Sec(group.Sum(c => c.Seconds))),
                new XAttribute("timestamp", group.Min(c => c.Start).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
                new XAttribute("hostname", Environment.MachineName));
            if (!string.IsNullOrWhiteSpace(environment)) suite.Add(new XElement("properties", Prop("environment", environment.Trim())));
            foreach (var c in group)
            {
                var tc = new XElement("testcase",
                    new XAttribute("classname", group.Key),
                    new XAttribute("name", c.Name),
                    new XAttribute("time", Sec(c.Seconds)));
                var props = new List<XElement>();
                if (c.TestCaseId.Length > 0) props.Add(Prop("testcaseid", c.TestCaseId));
                if (c.Tags.Count > 0) props.Add(Prop("tags", string.Join(", ", c.Tags)));
                if (c.DataLabel != null) props.Add(Prop("data", c.DataLabel));
                if (c.Attempts > 1) props.Add(Prop("attempts", c.Attempts.ToString(CultureInfo.InvariantCulture)));
                if (props.Count > 0) tc.Add(new XElement("properties", props));
                if (!c.Ok)
                {
                    var failed = c.Steps.Where(s => !s.Ok).ToList();
                    tc.Add(new XElement("failure",
                        new XAttribute("message", c.Message),
                        new XAttribute("type", failed.Any(s => s.IsAssert) ? "AssertionFailed" : "StepFailed"),
                        string.Join("\n", failed.Select(s => $"Bước {s.Number} [{s.JobName}] {s.Description}: {s.Detail}"))));
                }
                var flaky = c.Flaky ? $"Đạt sau khi chạy lại (lần {c.Attempts}) — lần đầu không đạt: {c.FirstFailure}\n" : "";
                tc.Add(new XElement("system-out", flaky + string.Join("\n", c.Steps.Select(s =>
                    $"{(s.Ok ? "✔" : "✖")} {new string(' ', s.Depth * 2)}{s.Number}. {s.Description}{(s.Detail.Length > 0 ? " — " + s.Detail : "")} ({Sec(s.Seconds)}s)"))));
                foreach (var shot in c.Steps.Where(s => s.Screenshot != null))
                    tc.Add(new XElement("system-err", $"[[ATTACHMENT|{shot.Screenshot}]]"));
                suite.Add(tc);
            }
            suites.Add(suite);
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), suites).Declaration + "\n" + suites;
    }

    // ───────────────────────────── HTML ─────────────────────────────

    private static string Html(string suiteName, IReadOnlyList<TestCaseResult> cases, string folder, string? environment)
    {
        // Chỉ thoát ký tự đặc biệt của HTML — WebUtility.HtmlEncode đổi cả chữ có dấu thành &#NNN; làm file khó đọc.
        static string E(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        int passed = cases.Count(c => c.Ok);
        bool allOk = passed == cases.Count && cases.Count > 0;
        var start = cases.Count > 0 ? cases.Min(c => c.Start) : DateTime.Now;

        var verdictClass = allOk ? "ok" : "bad";
        var verdict = allOk ? "ĐẠT" : "KHÔNG ĐẠT";
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html>
            <html lang="vi"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Báo cáo kiểm thử — {{E(suiteName)}}</title>
            <style>{{Css}}</style></head><body><main>
            <h1>Báo cáo kiểm thử — {{E(suiteName)}}</h1>
            <div class="muted">{{start:dd/MM/yyyy HH:mm:ss}} · máy {{E(Environment.MachineName)}} · {{E(Environment.UserName)}}{{(string.IsNullOrWhiteSpace(environment) ? "" : " · môi trường <b>" + E(environment.Trim()) + "</b>")}}</div>
            <div class="cards">
              <div class="card"><span class="muted">Kết quả</span><b class="{{verdictClass}}">{{verdict}}</b></div>
              <div class="card"><span class="muted">Kịch bản đạt</span><b>{{passed}} / {{cases.Count}}</b></div>
              <div class="card"><span class="muted">Kiểm tra (assert) đạt</span><b>{{cases.Sum(c => c.AssertsPassed)}} / {{cases.Sum(c => c.AssertsPassed + c.AssertsFailed)}}</b></div>
              <div class="card"><span class="muted">Thời gian</span><b>{{E(Duration(cases.Sum(c => c.Seconds)))}}</b></div>
            </div>
            """);

        foreach (var c in cases)
        {
            sb.Append($"""<details{(c.Ok ? "" : " open")}><summary><span class="badge {(c.Ok ? "ok" : "bad")}">{(c.Ok ? "✔ Đạt" : "✖ Không đạt")}</span>""");
            if (c.Flaky) sb.Append($"""<span class="badge flaky">↻ chạy lại {c.Attempts - 1} lần</span>""");
            sb.Append($"""<span class="name">{E(c.Name)}</span><span class="muted">{E(c.Group)}</span>""");
            if (c.TestCaseId.Length > 0) sb.Append($"""<span class="tag">#{E(c.TestCaseId)}</span>""");
            foreach (var tag in c.Tags) sb.Append($"""<span class="tag">{E(tag)}</span>""");
            sb.Append($"""<span class="muted">{c.AssertsPassed}/{c.AssertsPassed + c.AssertsFailed} kiểm tra · {E(Duration(c.Seconds))}</span></summary>""");
            var message = c.Flaky ? $"Đạt ở lần chạy thứ {c.Attempts} — lần đầu không đạt: {c.FirstFailure}" : c.Message;
            sb.Append($"""<div class="msg">{E(message)}</div><div class="wrap"><table><thead><tr><th>#</th><th>Bước</th><th>Kết quả</th><th>Chi tiết</th><th>Thời gian</th></tr></thead><tbody>""");
            foreach (var s in c.Steps)
            {
                var number = s.Depth > 0 ? $"↳ {E(s.JobName)} · {s.Number}" : s.Number.ToString(CultureInfo.InvariantCulture);
                sb.Append($"""<tr class="{(s.Ok ? "" : "fail")}"><td class="t">{number}</td>""");
                sb.Append($"""<td style="padding-left:{10 + s.Depth * 18}px">{(s.IsAssert ? AssertTag : "")}{E(s.Description)}</td>""");
                sb.Append($"""<td class="st {(s.Ok ? "ok" : "bad")}">{(s.Ok ? "✔" : "✖")}</td><td>{E(s.Detail)}""");
                if (s.Screenshot != null)
                {
                    var rel = Path.GetRelativePath(folder, s.Screenshot).Replace('\\', '/');
                    var href = rel.StartsWith("..", StringComparison.Ordinal) ? new Uri(s.Screenshot).AbsoluteUri : Uri.EscapeDataString(rel).Replace("%2F", "/");
                    sb.Append($"""<br><a href="{href}" target="_blank"><img class="shot" src="{href}" alt="Ảnh lúc lỗi" loading="lazy"></a>""");
                }
                sb.Append($"""</td><td class="t">{s.Seconds.ToString("0.0", CultureInfo.InvariantCulture)} s</td></tr>""");
            }
            if (c.Steps.Count == 0) sb.Append("""<tr><td colspan="5" class="muted">Không có bước nào được chạy.</td></tr>""");
            sb.Append("</tbody></table></div></details>");
        }
        sb.Append("<p class=\"muted\">Tạo bởi ScheduleApp — junit.xml cùng thư mục dùng cho Azure DevOps / Jenkins / GitHub Actions.</p></main></body></html>");
        return sb.ToString();
    }

    private const string AssertTag = """<span class="assert">ASSERT</span>""";

    private const string Css = """
        :root{--bg:#f6f7f9;--card:#fff;--text:#1d2329;--muted:#5f6b76;--line:#e1e5ea;--ok:#107c10;--okbg:#e7f4e7;--bad:#c42b1c;--badbg:#fbe9e7;--accent:#005a9e}
        @media (prefers-color-scheme:dark){:root{--bg:#16191d;--card:#1f2328;--text:#e6e9ed;--muted:#9aa5b1;--line:#30363d;--ok:#6ccb5f;--okbg:#1e3320;--bad:#ff8a7a;--badbg:#3a1f1c;--accent:#4aa3ff}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
        main{max-width:1100px;margin:0 auto;padding:24px 16px 48px}
        h1{font-size:22px;margin:0 0 4px}.muted{color:var(--muted)}
        .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin:18px 0 24px}
        .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px}
        .card b{display:block;font-size:22px}.ok{color:var(--ok)}.bad{color:var(--bad)}
        .badge{display:inline-block;padding:1px 9px;border-radius:999px;font-weight:600;font-size:12px}
        .badge.ok{background:var(--okbg)}.badge.bad{background:var(--badbg)}
        .badge.flaky{background:#fff4ce;color:#8a5a00}.tag{font-size:12px;border:1px solid var(--line);border-radius:999px;padding:0 8px;color:var(--muted)}
        details{background:var(--card);border:1px solid var(--line);border-radius:10px;margin:0 0 10px;overflow:hidden}
        summary{cursor:pointer;padding:12px 14px;display:flex;gap:10px;align-items:center;flex-wrap:wrap;list-style:none}
        summary::-webkit-details-marker{display:none}summary .name{font-weight:600;flex:1;min-width:200px}
        .wrap{overflow-x:auto;border-top:1px solid var(--line)}
        table{border-collapse:collapse;width:100%;font-size:13px}
        th,td{text-align:left;padding:6px 10px;border-bottom:1px solid var(--line);vertical-align:top}
        th{color:var(--muted);font-weight:600}tr.fail td{background:var(--badbg)}
        td.st{white-space:nowrap;font-weight:600}td.t{white-space:nowrap;color:var(--muted)}
        .assert{font-size:11px;border:1px solid var(--accent);color:var(--accent);border-radius:4px;padding:0 4px;margin-right:4px}
        img.shot{max-width:320px;width:100%;border:1px solid var(--line);border-radius:6px;margin-top:6px}
        .msg{padding:8px 14px;color:var(--muted)}
        """;

    internal static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} phút {t.Seconds:00} giây" : $"{t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} giây";
    }
}
