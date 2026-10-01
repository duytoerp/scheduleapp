using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Windows.Forms;
using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Data;
using ScheduleApp.Services.Engine;

namespace ScheduleApp.Tests;

/// <summary>
/// UI Automation trên một form đặt ngoài vùng màn hình và điều khiển Edge headless — không đụng chuột/bàn phím thật.
/// Chạy: set SCHEDULEAPP_LIVE_TESTS=1 rồi dotnet test.
/// </summary>
public class LiveTests
{
    [LiveFact]
    public void UiAutomationFindClickAndSetText()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunUia(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void RunUia()
    {
        var form = new Form
        {
            Text = "UIA Test Form",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(SystemInformation.VirtualScreen.Right + 300, SystemInformation.VirtualScreen.Top),
            Size = new Size(400, 200),
            ShowInTaskbar = false
        };
        int clicks = 0;
        var btn = new Button { Name = "btnLuu", Text = "Lưu", Location = new Point(10, 10) };
        btn.Click += (_, _) => clicks++;
        var txt = new TextBox { Name = "txtTen", Location = new Point(10, 50), Width = 200 };
        form.Controls.AddRange([btn, txt]);
        form.Show();
        Application.DoEvents();
        var hwnd = form.Handle;

        // UIA phải gọi từ luồng khác luồng UI của chính form (luồng UI tiếp tục bơm message).
        T Bg<T>(Func<T> f)
        {
            var t = Task.Run(f);
            while (!t.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); }
            return t.GetAwaiter().GetResult();
        }

        try
        {
            var byId = Bg(() => UiElementFinder.Find(hwnd, "AutomationId=btnLuu"));
            Assert.NotNull(byId);
            Assert.NotNull(Bg(() => UiElementFinder.Find(hwnd, "Name=lưu; ControlType=Button")));
            Assert.NotNull(Bg(() => UiElementFinder.Find(hwnd, "Name~=ư; ControlType=Button")));
            Assert.Null(Bg(() => UiElementFinder.Find(hwnd, "Name=Không có; ControlType=Button")));

            Bg(() => { UiElementFinder.ClickAsync(byId!, MouseButtonKind.Left, false, CancellationToken.None).GetAwaiter().GetResult(); return 0; });
            for (int i = 0; i < 20 && clicks == 0; i++) { Application.DoEvents(); Thread.Sleep(20); }
            Assert.Equal(1, clicks);

            var edit = Bg(() => UiElementFinder.Find(hwnd, "AutomationId=txtTen; ControlType=Edit"))!;
            Bg(() => { UiElementFinder.SetTextAsync(edit, "Nguyễn Văn Á 😀", CancellationToken.None).GetAwaiter().GetResult(); return 0; });
            Application.DoEvents();
            Assert.Equal("Nguyễn Văn Á 😀", txt.Text);
            Assert.Equal("Nguyễn Văn Á 😀", Bg(() => UiElementFinder.GetValue(edit)));
            Assert.Throws<FormatException>(() => UiElementFinder.Validate("Foo=bar"));
        }
        finally
        {
            form.Close();
            form.Dispose();
        }
    }

    /// <summary>
    /// Excel thật (chạy ẩn) tạo file có bảng + công thức → ScheduleApp thêm dòng / sửa dòng / thêm cột → Excel mở lại đọc đúng giá trị.
    /// Bỏ qua nếu máy không cài Excel.
    /// </summary>
    [LiveFact]
    public void ExcelOpensFilesWrittenByScheduleApp()
    {
        var excelType = Type.GetTypeFromProgID("Excel.Application");
        if (excelType == null) return;
        var dir = TestSupport.NewDir();
        var path = Path.Combine(dir, "tu-excel.xlsx");
        var fresh = Path.Combine(dir, "moi-tao.xlsx");
        List<KeyValuePair<string, string>> V(params (string K, string V)[] values) => values.Select(v => new KeyValuePair<string, string>(v.K, v.V)).ToList();

        dynamic xl = Activator.CreateInstance(excelType)!;
        try
        {
            xl.Visible = false;
            xl.DisplayAlerts = false;
            dynamic wb = xl.Workbooks.Add();
            dynamic ws = wb.Worksheets[1];
            ws.Name = "Dữ liệu";
            string[] heads = ["Mã", "Tên", "Số tiền", "Gấp đôi"];
            for (int c = 0; c < 4; c++) ws.Cells[1, c + 1].Value2 = heads[c];
            for (int r = 2; r <= 4; r++)
            {
                ws.Cells[r, 1].Value2 = $"KH00{r - 1}";
                ws.Cells[r, 2].Value2 = $"Khách {r - 1}";
                ws.Cells[r, 3].Value2 = r * 100;
                ws.Cells[r, 4].Formula = $"=C{r}*2";
            }
            ws.ListObjects.Add(1, ws.Range["A1:D4"], Type.Missing, 1); // Format as Table
            wb.SaveAs(path, 51);
            wb.Close(false);

            TabularWriter.Write(path, "Dữ liệu", DataAction.AppendRow, "", V(("Mã", "KH004"), ("Tên", "Đỗ Thị Ê"), ("Số tiền", "400"), ("TrangThai", "Mới")));
            TabularWriter.Write(path, null, DataAction.UpdateRow, "Mã=KH002", V(("TrangThai", "Đã nhập"), ("Gấp đôi", "0")));
            TabularWriter.Write(fresh, "Kết quả", DataAction.AppendRow, "", V(("Ngày", "01/10/2026"), ("Tiền", "1500.5"), ("Ghi chú", "dòng 1\ndòng 2")));

            wb = xl.Workbooks.Open(path);
            ws = wb.Worksheets["Dữ liệu"];
            Assert.Equal("KH004", (string)ws.Range["A5"].Value2);
            Assert.Equal("Đỗ Thị Ê", (string)ws.Range["B5"].Value2);
            Assert.Equal(400d, (double)ws.Range["C5"].Value2);   // số ghi dạng số
            Assert.Equal("TrangThai", (string)ws.Range["E1"].Value2);
            Assert.Equal("Mới", (string)ws.Range["E5"].Value2);
            Assert.Equal("Đã nhập", (string)ws.Range["E3"].Value2);
            Assert.Equal(0d, (double)ws.Range["D3"].Value2);    // công thức bị thay bằng giá trị
            Assert.Equal(800d, (double)ws.Range["D4"].Value2);  // công thức khác vẫn tính
            Assert.Equal("$A$1:$D$5", (string)ws.ListObjects[1].Range.Address); // bảng được nới xuống dòng mới
            wb.Close(false);

            wb = xl.Workbooks.Open(fresh);
            ws = wb.Worksheets["Kết quả"];
            Assert.Equal("Ngày", (string)ws.Range["A1"].Value2);
            Assert.Equal(1500.5d, (double)ws.Range["B2"].Value2);
            Assert.Equal("dòng 1\ndòng 2", (string)ws.Range["C2"].Value2);
            wb.Close(false);
        }
        finally
        {
            int pid = 0;
            try { pid = ExcelPid((IntPtr)(int)xl.Hwnd); } catch (Exception) { }
            xl.Quit();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(xl);
            // Các đối tượng COM trung gian (workbook, range…) chỉ được giải phóng khi GC chạy — nếu không Excel ẩn vẫn còn chạy.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (pid > 0)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (!p.WaitForExit(5000)) p.Kill();
                }
                catch (ArgumentException) { } // đã thoát
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    private static int ExcelPid(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out int pid);
        return pid;
    }

    [LiveFact]
    public async Task BrowserDevToolsProtocol()
    {
        var edge = new[]
        {
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe")
        }.FirstOrDefault(File.Exists);
        Assert.True(edge != null, "Máy không có Microsoft Edge.");

        const int port = 9333;
        SettingsStore.Current.BrowserPort = port;
        var profile = Path.Combine(TestSupport.NewDir(), "edge-headless");
        var html = Uri.EscapeDataString("<html><head><title>Trang thử</title></head><body>" +
                   "<input id='ten' name='ten'><select id='tp'><option>HN</option><option>HCM</option></select>" +
                   "<button id='luu' onclick=\"document.getElementById('kq').innerText='Đã lưu: '+document.getElementById('ten').value\">Lưu lại</button>" +
                   "<div id='kq'></div></body></html>");
        var proc = Process.Start(new ProcessStartInfo(edge!,
            $"--headless=new --remote-debugging-port={port} --user-data-dir=\"{profile}\" --no-first-run \"data:text/html;charset=utf-8,{html}\"") { UseShellExecute = false });
        try
        {
            using var http = new HttpClient();
            for (int i = 0; i < 50; i++)
            {
                try { if ((await http.GetAsync($"http://127.0.0.1:{port}/json/version")).IsSuccessStatusCode) break; } catch (HttpRequestException) { }
                await Task.Delay(200);
            }
            await Task.Delay(800);

            var job = new Job { Name = "browser" };
            var ctx = new FlowContext(job, new FakeUi(), RunOptions.Default, _ => null, CancellationToken.None);
            ctx.Vars["ten"] = "Trần Thị Ổi";
            async Task Do(BrowserAction a, string text, string args = "", string variable = "", int timeout = 5000)
            {
                var s = ActionStep.CreateDefault(StepType.Browser);
                s.BrowserAction = a; s.Text = text; s.Arguments = args; s.Variable = variable; s.Target = "Trang thử"; s.DelayMs = timeout;
                await StepExecutor.ExecuteAsync(ctx.ExpandStep(s), job, ctx);
            }

            await Do(BrowserAction.SetValue, "#ten", "{{ten}}");
            await Do(BrowserAction.SetValue, "select#tp", "HCM");
            await Do(BrowserAction.Click, "text:Lưu lại");
            await Do(BrowserAction.ReadText, "#kq", "", "kq");
            Assert.Equal("Đã lưu: Trần Thị Ổi", ctx.Vars["kq"]);
            await Do(BrowserAction.ReadText, "select#tp", "", "tp");
            Assert.Equal("HCM", ctx.Vars["tp"]);
            await Do(BrowserAction.RunScript, "document.querySelectorAll('option').length * 10", "", "js");
            Assert.Equal("20", ctx.Vars["js"]);
            await Do(BrowserAction.ReadText, "xpath://button[@id='luu']", "", "x");
            Assert.Equal("Lưu lại", ctx.Vars["x"]);
            await Assert.ThrowsAsync<TimeoutException>(() => Do(BrowserAction.WaitFor, "#khongco", timeout: 800));
        }
        finally
        {
            try { proc?.Kill(true); } catch (InvalidOperationException) { }
        }
    }
}
