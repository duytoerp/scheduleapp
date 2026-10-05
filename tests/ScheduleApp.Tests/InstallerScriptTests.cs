namespace ScheduleApp.Tests;

/// <summary>
/// Bộ cài (installer\ScheduleApp.iss): máy chạy kiểm thử không có Inno Setup nên chỉ kiểm tra nội dung kịch bản — tìm .NET x64 đúng chỗ
/// trên máy ARM64, cài ngầm không hiện UAC / không tự khởi động lại máy, không còn nút Hủy lúc bộ cài .NET đang chạy.
/// </summary>
public class InstallerScriptTests
{
    private static string Script()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "installer", "ScheduleApp.iss");
            if (File.Exists(path)) return File.ReadAllText(path).ReplaceLineEndings("\n");
        }
        throw new FileNotFoundException("Không thấy installer\\ScheduleApp.iss");
    }

    /// <summary>Thân hàm / thủ tục Pascal <paramref name="name"/> (từ dòng khai báo tới "end;" đầu dòng).</summary>
    private static string Body(string script, string name)
    {
        int start = script.IndexOf($"function {name}(", StringComparison.Ordinal);
        if (start < 0) start = script.IndexOf($"procedure {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, "Không thấy " + name);
        int end = script.IndexOf("\nend;", start, StringComparison.Ordinal);
        return script[start..end];
    }

    [Fact]
    public void Arm64LooksOnlyForTheX64Runtime()
    {
        var body = Body(Script(), "DesktopRuntimeInstalled");
        // Máy ARM64: dotnet\ là .NET ARM64 — ScheduleApp x64 cần bản trong dotnet\x64.
        Assert.Matches(@"if IsARM64 then\s+Result := HasDesktopRuntime10\(ExpandConstant\('\{commonpf64\}\\dotnet\\x64'\)\)\s+else\s+Result := HasDesktopRuntime10\(ExpandConstant\('\{commonpf64\}\\dotnet'\)\);", body);
        Assert.Contains(@"RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Location)", body);
    }

    [Fact]
    public void SilentPerUserInstallSkipsDotnetAndNeverReboots()
    {
        var script = Script();
        var init = Body(script, "InitializeSetup");
        Assert.Contains("if WizardSilent and not IsAdminInstallMode and not InstallDotnetSwitch() then", init);
        Assert.True(init.IndexOf("RuntimeWanted := False", StringComparison.Ordinal) < init.IndexOf("SuppressibleMsgBox", StringComparison.Ordinal));
        var sw = Body(script, "InstallDotnetSwitch");
        Assert.Contains("ExpandConstant('{param:INSTALLDOTNET|0}') <> '0'", sw);
        Assert.Contains("CompareText(ParamStr(I), '/INSTALLDOTNET') = 0", sw);
        Assert.Contains("Result := RuntimeRestart and not WizardSilent;", Body(script, "NeedRestart"));
        Assert.Contains("english.RuntimeSilentSkipped=", script);
    }

    [Fact]
    public void CancelButtonIsHiddenWhileTheDotnetInstallerRuns()
    {
        var body = Body(Script(), "InstallRuntime");
        int hide = body.IndexOf("DownloadPage.AbortButton.Hide;", StringComparison.Ordinal);
        Assert.True(hide > body.IndexOf("Downloaded := True;", StringComparison.Ordinal));
        Assert.True(hide >= 0 && hide < body.IndexOf("ShellExec(", StringComparison.Ordinal));
    }
}
