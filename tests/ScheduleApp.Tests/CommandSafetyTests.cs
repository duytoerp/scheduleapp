using System.Runtime.InteropServices;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>
/// Bước "Chạy lệnh" chạy qua cmd.exe THẬT: dữ liệu không tin cậy (tên file, nội dung do người/email/web đưa vào)
/// không được thoát khỏi dấu nháy để chạy như lệnh, nhưng các toán tử shell người dùng CỐ Ý viết (&amp;, |, &gt;, ngoặc,
/// %VAR%) vẫn hoạt động. Dùng file / tiến trình thật, không giả lập.
/// </summary>
public class CommandSafetyTests
{
    /// <summary>Chạy một flow và trả về output của bước RunCommand (biến "out").</summary>
    private static async Task<string> Run(params ActionStep[] steps)
    {
        var (r, c) = await RunAsync(new Job { Name = "an-toan-lenh", Steps = [.. steps] });
        Assert.True(r.Ok, r.Message);
        return c.Vars.TryGetValue("out", out var o) ? o : "";
    }

    private static ActionStep Cmd(string target) => S(StepType.RunCommand, s => { s.Target = target; s.Variable = "out"; });

    /// <summary>Có một dòng (sau khi bỏ khoảng trắng) bằng đúng <paramref name="text"/> không — dấu hiệu lệnh đã chạy.</summary>
    private static bool HasLine(string output, string text) =>
        output.Replace("\r", "").Split('\n').Any(l => l.Trim() == text);

    // ───────────────────────────── Chống chèn lệnh ─────────────────────────────

    [Fact]
    public async Task DataInsideQuotesCannotBreakOutAndRunCommands()
    {
        var dir = NewDir();
        var sentinel = Path.Combine(dir, "pwned.txt");
        // Kẻ tấn công cố thoát khỏi dấu nháy bằng & và > để ghi file. Lớp cmd ngoài KHÔNG được hiểu đây là lệnh.
        var output = await Run(Cmd($"echo \"a & echo X> {sentinel} & echo b\""));
        Assert.False(File.Exists(sentinel));                         // không có lệnh phụ nào chạy
        Assert.Contains("a & echo", output);                         // dữ liệu được in ra nguyên văn
    }

    [Fact]
    public async Task EchoWithQuotedAmpersandNeverRunsASecondCommand()
    {
        var output = await Run(Cmd("echo \"a&echo INJECTED\""));
        Assert.Contains("a&echo INJECTED", output.Replace("\r", "").Replace("\n", ""));
        Assert.False(HasLine(output, "INJECTED"));                   // không có dòng "INJECTED" riêng
    }

    [Fact]
    public async Task CmdFormatterNeutralisesHostileData()
    {
        var dir = NewDir();
        var sentinel = Path.Combine(dir, "pwned.txt");
        // Giá trị độc hại có cả dấu nháy để thử phá chuỗi; {{v:cmd}} tự bọc nháy và bỏ dấu nháy bên trong.
        var output = await Run(
            SetVar("v", VarSource.Value, $"a\" & echo PWNED> {sentinel} & echo \""),
            Cmd("echo {{v:cmd}}"));
        Assert.False(File.Exists(sentinel));
        Assert.False(HasLine(output, "PWNED"));

        var plain = await Run(SetVar("v", VarSource.Value, "a\" & echo PWNED & \""), Cmd("echo {{v:cmd}}"));
        Assert.False(HasLine(plain, "PWNED"));
        Assert.Contains("PWNED", plain);                             // in ra như dữ liệu, không chạy
    }

    [Fact]
    public void CmdFormatterQuotesAndStripsQuotesAndNewlines()
    {
        Assert.Equal("\"a & b\"", VariableExpander.ApplyFormat("a & b", "cmd"));
        Assert.Equal("\"ab & echo xy\"", VariableExpander.ApplyFormat("a\"b & echo x\r\ny", "CMD"));
        Assert.Equal("\"\"", VariableExpander.ApplyFormat("", "cmd"));
        // \ cuối được nhân đôi để \" không thành dấu nháy thường với chương trình đọc tham số kiểu C.
        Assert.Equal("\"D:\\\\\"", VariableExpander.ApplyFormat("D:\\", "cmd"));
        Assert.Equal("\"C:\\a\\b\\\\\\\\\"", VariableExpander.ApplyFormat("C:\\a\\b\\\\", "cmd"));
        Assert.Equal("\"C:\\a\\b\"", VariableExpander.ApplyFormat("C:\\a\\b", "cmd"));   // \ ở giữa giữ nguyên
    }

    [Fact]
    public async Task TrailingBackslashFolderStaysASeparateArgumentForRobocopy()
    {
        var dir = NewDir();
        var src = Path.Combine(dir, "nguon") + "\\";               // thư mục chọn từ hộp thoại / gốc ổ đĩa có \ cuối
        var dst = Path.Combine(dir, "dich") + "\\";
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "noi dung");
        // robocopy đọc tham số kiểu C: "…\nguon\" sẽ nuốt dấu nháy và dính luôn đích → lỗi / chép sai chỗ.
        await Run(
            SetVar("nguon", VarSource.Value, src),
            SetVar("dich", VarSource.Value, dst),
            Cmd("robocopy {{nguon:cmd}} {{dich:cmd}} /E /R:0 /W:0 /NJH /NJS & if errorlevel 8 (exit /b 1) else (exit /b 0)"));
        Assert.Equal("noi dung", File.ReadAllText(Path.Combine(dst, "a.txt")));
    }

    [Fact]
    public async Task CmdBuiltinsAcceptDoubledTrailingBackslash()
    {
        var dir = NewDir();
        var dest = Path.Combine(dir, "dich") + "\\";
        Directory.CreateDirectory(dest);
        var file = Path.Combine(dir, "tep (1).txt");
        File.WriteAllText(file, "x");
        var output = await Run(
            SetVar("f", VarSource.Value, file),
            SetVar("d", VarSource.Value, dest),
            Cmd("move /Y {{f:cmd}} {{d:cmd}} >nul & dir /b {{d:cmd}}"));
        Assert.True(File.Exists(Path.Combine(dest, "tep (1).txt")));
        Assert.True(HasLine(output, "tep (1).txt"));
    }

    [Fact]
    public void BuiltInVariablesAcceptFormats()
    {
        var x = new VariableExpander([]);
        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{4}-", x.Expand("{{guid:upper}}"));
        Assert.Equal("1", x.Expand("{{newline:len}}"));
        Assert.Equal("\"\t\"", x.Expand("{{tab:cmd}}"));

        var before = Environment.GetEnvironmentVariable("SCHEDULEAPP_CMDTEST");
        Environment.SetEnvironmentVariable("SCHEDULEAPP_CMDTEST", "C:\\Thu muc\\");
        try
        {
            Assert.Equal("\"C:\\Thu muc\\\\\"", x.Expand("{{env:SCHEDULEAPP_CMDTEST:cmd}}"));
            Assert.Equal("C:\\Thu muc\\", x.Expand("{{env:SCHEDULEAPP_CMDTEST}}"));
        }
        finally { Environment.SetEnvironmentVariable("SCHEDULEAPP_CMDTEST", before); }
    }

    [Fact]
    public async Task ClipboardWithCmdFormatRunsAsData()
    {
        var userText = ClipboardHelper.TryGetText();
        try
        {
            // Prompt AI bắt dùng {{clipboard:cmd}} — trước đây báo "chưa được gán giá trị".
            ClipboardHelper.SetText("a\" & echo PWNED & \"");
            var output = await Run(Cmd("echo {{clipboard:cmd}}"));
            Assert.False(HasLine(output, "PWNED"));
            Assert.True(HasLine(output, "\"a & echo PWNED & \""));
        }
        finally { RestoreClipboard(userText); }
    }

    // ───────────────────────────── Giữ nguyên toán tử cố ý ─────────────────────────────

    [Fact]
    public async Task IntentionalShellOperatorsStillWork()
    {
        var both = await Run(Cmd("echo A & echo B"));
        Assert.True(HasLine(both, "A"));
        Assert.True(HasLine(both, "B"));

        var piped = await Run(Cmd("echo xyzzy | findstr xyzzy"));
        Assert.True(HasLine(piped, "xyzzy"));

        var grouped = await Run(Cmd("(echo P1) & if 1==1 (echo P2) else (echo P3)"));
        Assert.True(HasLine(grouped, "P1"));
        Assert.True(HasLine(grouped, "P2"));
        Assert.False(HasLine(grouped, "P3"));

        var escaped = await Run(Cmd("echo a^&b"));                  // ^ cố ý của người dùng vẫn giữ tác dụng
        Assert.True(HasLine(escaped, "a&b"));
    }

    [Fact]
    public async Task RedirectionWritesRealFile()
    {
        var dir = NewDir();
        var file = Path.Combine(dir, "co dau cach & ky tu (1).txt");
        await Run(Cmd($"echo noi-dung> \"{file}\""));
        Assert.Equal("noi-dung", File.ReadAllText(file).Trim());
    }

    [Fact]
    public async Task EnvironmentVariableStillExpands()
    {
        var before = Environment.GetEnvironmentVariable("SCHEDULEAPP_CMDTEST");
        Environment.SetEnvironmentVariable("SCHEDULEAPP_CMDTEST", "XYZ_890");
        try
        {
            var output = await Run(Cmd("echo %SCHEDULEAPP_CMDTEST%"));
            Assert.True(HasLine(output, "XYZ_890"));
        }
        finally { Environment.SetEnvironmentVariable("SCHEDULEAPP_CMDTEST", before); }
    }

    [Fact]
    public async Task SpecialCharsAndUtf8RoundTripInsideQuotes()
    {
        var special = await Run(Cmd("echo \"(a)^b<c>|d\""));
        Assert.True(HasLine(special, "\"(a)^b<c>|d\""));

        var viet = await Run(Cmd("echo Tiếng Việt"));
        Assert.True(HasLine(viet, "Tiếng Việt"));
    }

    [Fact]
    public async Task ExitCodeOfLastCommandStillReportsFailure()
    {
        var (r, _) = await RunAsync(new Job { Name = "loi", Steps = [S(StepType.RunCommand, s => s.Target = "echo x & exit 3")] });
        Assert.False(r.Ok);
        Assert.Contains("mã 3", r.Message);
    }

    // ───────────────────────────── Lệnh trong file mẫu + tên file độc hại ─────────────────────────────

    [Fact]
    public async Task SampleMoveShapeWithHostileFileNameMovesFileAndDoesNotInject()
    {
        var dir = NewDir();
        // Tên file tải về có thể chứa & ( ) ^ khoảng trắng (Windows cho phép) — mô phỏng file độc hại.
        var src = Path.Combine(dir, "hoa don & echo INJECTED (1) ^ x.pdf");
        File.WriteAllText(src, "noi dung");
        var destDir = Path.Combine(dir, "HoaDon");
        // Đúng dạng lệnh mẫu A4: mkdir "…" 2>nul & move /Y {{trigger.file:cmd}} "…\".
        var output = await Run(
            SetVar("f", VarSource.Value, src),
            Cmd($"mkdir \"{destDir}\" 2>nul & move /Y {{{{f:cmd}}}} \"{destDir}\\\""));
        Assert.True(File.Exists(Path.Combine(destDir, Path.GetFileName(src))));   // đã chuyển đúng file
        Assert.False(File.Exists(src));
        Assert.False(HasLine(output, "INJECTED"));
    }

    [Fact]
    public async Task SampleRenShapeWithHostileFileNameRenamesAndDoesNotInject()
    {
        var dir = NewDir();
        var src = Path.Combine(dir, "anh & echo INJECTED.jpg");
        File.WriteAllText(src, "noi dung");
        // Đúng dạng lệnh mẫu: ren {{f:cmd}} "{{loop.index:000}}_{{f.name}}" — tên file vẫn nằm trong dấu nháy.
        var output = await Run(
            SetVar("f", VarSource.Value, src),
            SetVar("ten", VarSource.Value, Path.GetFileName(src)),
            Cmd("ren {{f:cmd}} \"001_{{ten}}\""));
        Assert.True(File.Exists(Path.Combine(dir, "001_anh & echo INJECTED.jpg")));
        Assert.False(File.Exists(src));
        Assert.False(HasLine(output, "INJECTED"));
    }

    [Fact]
    public async Task SampleNoteStepWritesNoteExactlyWithoutQuotesOrInjection()
    {
        var dir = NewDir();
        var log = Path.Combine(dir, "ghi-chu.txt");
        var sentinel = Path.Combine(dir, "pwned.txt");
        // Đúng bước ghi của mẫu A5 (không qua cmd), chỉ đổi file đích sang thư mục test.
        var step = SampleJobs().Single(j => j.Name.StartsWith("A5 ")).Steps.Single(s => s.Type == StepType.WriteData).Clone();
        step.Target = log;
        var note = $"mua sữa & echo X> \"{sentinel}\"";
        await Run(SetVar("ghiChu", VarSource.Value, note), step);
        await Run(SetVar("ghiChu", VarSource.Value, "mua sữa"), step);
        Assert.False(File.Exists(sentinel));
        var lines = File.ReadAllLines(log);
        Assert.Equal(2, lines.Length);
        // Đúng nguyên văn: "dd/MM/yyyy HH:mm:ss - <ghi chú>", không bị bọc dấu nháy như khi echo {{ghiChu:cmd}}.
        Assert.Matches(@"^\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2} - " + System.Text.RegularExpressions.Regex.Escape(note) + "$", lines[0]);
        Assert.Matches(@"^\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2} - mua sữa$", lines[1]);
    }

    private static IEnumerable<Job> SampleJobs() =>
        typeof(Job).Assembly.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n =>
            {
                using var stream = typeof(Job).Assembly.GetManifestResourceStream(n)!;
                return JobStore.ImportJson(new StreamReader(stream).ReadToEnd());
            });

    [Fact]
    public async Task RobocopyStyleErrorLevelCheckSeesTheRealExitCode()
    {
        // Mẫu sao lưu: "%ERRORLEVEL%" bị thay lúc ĐỌC dòng lệnh (trước khi robocopy chạy) nên luôn là 0 → lỗi bị nuốt.
        // "if errorlevel 8" kiểm tra lúc chạy → đúng.
        var (bad, _) = await RunAsync(new Job { Steps = [S(StepType.RunCommand, s => s.Target = "cmd /c exit 9 & if errorlevel 8 (exit /b 1) else (exit /b 0)")] });
        Assert.False(bad.Ok);
        var (ok, _) = await RunAsync(new Job { Steps = [S(StepType.RunCommand, s => s.Target = "cmd /c exit 3 & if errorlevel 8 (exit /b 1) else (exit /b 0)")] });
        Assert.True(ok.Ok, ok.Message);
    }

    // ───────────────────────────── Mẫu đã phát hành dùng {{biến:cmd}} ─────────────────────────────

    [Fact]
    public void ShippedSamplesUseCmdFormatterForDataVariables()
    {
        var commands = SampleJobs()
            .SelectMany(j => j.Steps)
            .Where(s => s.Type == StepType.RunCommand || (s.Type == StepType.SetVariable && s.VarSource == VarSource.Command))
            .Select(s => s.Target).ToList();

        Assert.Contains(commands, t => t.Contains("{{trigger.file:cmd}}"));
        Assert.Contains(commands, t => t.Contains("{{f:cmd}}"));
        // Mẫu ghi chú A5 ghi bằng bước "Ghi file" (thêm vào cuối) — không qua cmd, không bị bọc dấu nháy.
        var note = SampleJobs().Single(j => j.Name.StartsWith("A5 ")).Steps.Single(s => s.Type == StepType.WriteData);
        Assert.Equal(DataAction.AppendText, note.DataAction);
        Assert.Equal("{{now}} - {{ghiChu}}", note.Text);
        // Không còn biến dữ liệu tự bọc dấu nháy hay để trần (dễ bị chèn lệnh); không còn %ERRORLEVEL% đọc sai lúc phân tích.
        Assert.DoesNotContain(commands, t => t.Contains("\"{{trigger.file}}\"") || t.Contains("\"{{f}}\"") || t.Contains("{{ghiChu}}"));
        Assert.DoesNotContain(commands, t => t.Contains("%ERRORLEVEL%", StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────────── Hàm trung hòa (đơn vị) ─────────────────────────────

    [Theory]
    [InlineData("echo \"a&b\"", "echo \"a^&b\"")]           // & trong nháy (ngoài, theo lớp ngoài) → ^&
    [InlineData("echo A & echo B", "echo A & echo B")]       // & ngoài nháy (trong, theo lớp ngoài) → giữ nguyên
    [InlineData("robocopy \"a\" \"b\" & exit 0", "robocopy \"a\" \"b\" & exit 0")]
    [InlineData("echo \"(x)^y<z>|w\"", "echo \"^(x^)^^y^<z^>^|w\"")]
    public void EscapeForOuterCmdInvertsQuoteState(string input, string expected) =>
        Assert.Equal(expected, StepExecutor.EscapeForOuterCmd(input));

    // ───────────────────────────── Clipboard: không để lộ mật khẩu ─────────────────────────────

    [Fact]
    public async Task PastedSecretIsPrivateAndPreviousTextComesBack()
    {
        var userText = ClipboardHelper.TryGetText();
        try
        {
            ClipboardHelper.SetText("chu-cu-cua-nguoi-dung");
            (string? Text, bool Exclude, byte[]? History, byte[]? Cloud) during = default;
            await ClipboardHelper.PasteTemporarilyAsync("MatKhau#123", () => during = ReadClipboard(), CancellationToken.None, settleMs: 10);

            Assert.Equal("MatKhau#123", during.Text);
            Assert.True(during.Exclude);                             // trình theo dõi clipboard bỏ qua
            Assert.Equal(new byte[4], during.History);               // CanIncludeInClipboardHistory = 0 → không vào Win+V
            Assert.Equal(new byte[4], during.Cloud);                 // CanUploadToCloudClipboard = 0 → không đồng bộ đám mây

            var after = ReadClipboard();
            Assert.Equal("chu-cu-cua-nguoi-dung", after.Text);       // chữ cũ trở lại, không còn mật khẩu
            Assert.False(after.Exclude);                             // chữ cũ là dữ liệu bình thường
            Assert.Null(after.History);
        }
        finally { RestoreClipboard(userText); }
    }

    [Fact]
    public async Task PastedSecretIsClearedWhenClipboardWasEmpty()
    {
        var userText = ClipboardHelper.TryGetText();
        try
        {
            ClipboardHelper.Clear();
            string? during = null;
            await ClipboardHelper.PasteTemporarilyAsync("MatKhau#456", () => during = ReadClipboard().Text, CancellationToken.None, settleMs: 10);
            Assert.Equal("MatKhau#456", during);
            Assert.Null(ClipboardHelper.TryGetText());               // trước trống → sau cũng trống, không còn mật khẩu
        }
        finally { RestoreClipboard(userText); }
    }

    [Fact]
    public async Task PastedSecretIsClearedEvenWhenRunIsCancelled()
    {
        var userText = ClipboardHelper.TryGetText();
        try
        {
            ClipboardHelper.SetText("chu-cu-2");
            using var cts = new CancellationTokenSource();
            // Người dùng bấm Dừng ngay lúc đang chờ ứng dụng đích đọc clipboard.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ClipboardHelper.PasteTemporarilyAsync("MatKhau#789", cts.Cancel, cts.Token, settleMs: 5000));
            Assert.Equal("chu-cu-2", ClipboardHelper.TryGetText());
        }
        finally { RestoreClipboard(userText); }
    }

    private static void RestoreClipboard(string? userText)
    {
        if (userText != null) ClipboardHelper.SetText(userText);
        else ClipboardHelper.Clear();
    }

    /// <summary>Đọc thẳng clipboard của Windows (Win32) — chữ và các format riêng tư, không qua lớp WinForms đang được kiểm tra.</summary>
    private static (string? Text, bool Exclude, byte[]? History, byte[]? Cloud) ReadClipboard()
    {
        for (int attempt = 0; !OpenClipboard(IntPtr.Zero); attempt++)
        {
            if (attempt > 20) throw new InvalidOperationException("Không mở được clipboard.");
            Thread.Sleep(20);
        }
        try
        {
            const uint CfUnicodeText = 13;
            var textHandle = GetClipboardData(CfUnicodeText);
            string? text = textHandle == IntPtr.Zero ? null : WithLock(textHandle, p => Marshal.PtrToStringUni(p));
            bool exclude = IsClipboardFormatAvailable(RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"));
            return (text, exclude, Dword("CanIncludeInClipboardHistory"), Dword("CanUploadToCloudClipboard"));
        }
        finally { CloseClipboard(); }

        static byte[]? Dword(string format)
        {
            var h = GetClipboardData(RegisterClipboardFormat(format));
            if (h == IntPtr.Zero) return null;
            return WithLock(h, p =>
            {
                var bytes = new byte[4];
                Marshal.Copy(p, bytes, 0, 4);
                return bytes;
            });
        }

        static T WithLock<T>(IntPtr h, Func<IntPtr, T> read)
        {
            var p = GlobalLock(h);
            try { return read(p); }
            finally { GlobalUnlock(h); }
        }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
}
