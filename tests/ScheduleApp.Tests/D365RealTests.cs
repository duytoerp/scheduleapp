using ScheduleApp.Automation;
using ScheduleApp.Models;
using Xunit.Abstractions;

namespace ScheduleApp.Tests;

/// <summary>
/// Chỉ chạy khi có môi trường Dynamics 365 thật (biến môi trường SCHEDULEAPP_D365_URL) — kiểm chứng phần dựa vào cấu trúc trang
/// của Unified Interface mà máy chủ giả lập không chứng minh được (nút thanh lệnh theo command id, form selector, subgrid, view,
/// trang đăng nhập Microsoft). Dùng môi trường TEST, không dùng môi trường thật đang chạy nghiệp vụ: bài kiểm thử tạo rồi xóa
/// khách hàng / liên hệ có tên bắt đầu bằng "ScheduleApp RealTest".
/// </summary>
/// <remarks>
/// Biến môi trường:
///   SCHEDULEAPP_D365_URL           URL app, vd https://org.crm5.dynamics.com/main.aspx?appid=… (bắt buộc)
///   SCHEDULEAPP_D365_PROFILE_DIR   thư mục hồ sơ Edge đã đăng nhập sẵn (tùy chọn; trống = hồ sơ mới, cần tài khoản bên dưới)
///   SCHEDULEAPP_D365_USER / _PASSWORD / _TOTP   tài khoản test để bước "Đăng nhập Microsoft" tự đăng nhập (tùy chọn)
///   SCHEDULEAPP_D365_HEADLESS=0    hiện cửa sổ trình duyệt (mặc định chạy ẩn)
/// Chạy: dotnet test --filter FullyQualifiedName~D365RealTests
/// </remarks>
public sealed class D365RealTests(ITestOutputHelper output)
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    [RealD365Fact]
    public async Task UnifiedInterfaceEndToEnd()
    {
        var url = Env("SCHEDULEAPP_D365_URL")!;
        await using var browser = await LiveBrowser.StartAsync(9341, url, Env("SCHEDULEAPP_D365_PROFILE_DIR"), Env("SCHEDULEAPP_D365_HEADLESS") != "0");
        var d = new D365Steps();
        var name = $"ScheduleApp RealTest {DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            // 1. Đăng nhập (nếu có tài khoản test) — xong khi trang D365 có Xrm.
            if (Env("SCHEDULEAPP_D365_USER") is { } user)
            {
                d.Ctx.Vars["mk"] = Env("SCHEDULEAPP_D365_PASSWORD") ?? "";
                d.Ctx.Vars["totp"] = Env("SCHEDULEAPP_D365_TOTP") ?? "";
                await d.Do(D365Action.Login, user, "{{mk}}", rowRef: "{{totp}}", timeout: 120_000);
            }
            await d.Do(D365Action.OpenView, "account", timeout: 90_000);

            await d.Do(D365Action.GetUser, variable: "u");
            output.WriteLine($"Người dùng: {d["u"]} · vai trò: {d["d365.roles"].Replace('\n', ',')}");
            Assert.NotEmpty(d["d365.roles"]);

            // 2. Form tạo mới: đọc cấu trúc, nút thanh lệnh theo command id, form selector
            await d.Do(D365Action.OpenForm, "account", timeout: 60_000);
            var form = await D365Client.DescribeFormAsync("", CancellationToken.None);
            output.WriteLine($"Form account: {form.Fields.Count} field, tab: {string.Join(", ", form.Tabs.Select(t => t.Label))}");
            output.WriteLine($"Nút: {string.Join(" | ", form.Commands)}");
            output.WriteLine($"Subgrid: {string.Join(", ", form.Subgrids.Select(g => $"{g.Name} ({g.Label}, {g.Entity})"))}");
            Assert.Contains(form.Fields, f => f.Name == "name" && f.Required == "required");
            await d.Check(ConditionKind.D365Command, "Mscrm.Form.account.Save", "visible", wait: 15_000);
            await d.Check(ConditionKind.D365Command, "Mscrm.Form.account.Delete", "visible", negate: true, wait: 0);
            await d.Check(ConditionKind.D365CurrentForm, "", "", CompareOp.IsNotEmpty);

            // 3. Nhập, lưu, kiểm tra; thông báo khi lưu thiếu field bắt buộc
            var save = await Assert.ThrowsAsync<InvalidOperationException>(() => d.Do(D365Action.Save, timeout: 30_000));
            output.WriteLine("Lưu khi thiếu tên: " + save.Message);
            await d.Check(ConditionKind.D365Notification, "", wait: 10_000);
            await d.Do(D365Action.SetField, "name", name);
            await d.Do(D365Action.Save, variable: "accountId", timeout: 60_000);
            await d.Check(ConditionKind.D365FieldValue, "name", name);
            await d.Check(ConditionKind.D365RecordCount, $"accounts?$select=name&$filter=accountid eq {d["accountId"]}", "1");

            // 4. View mặc định (FetchXML của view + tìm theo tên) thấy bản ghi vừa tạo, mở được từ view
            await d.Do(D365Action.ViewQuery, "account", "", "n", rowRef: name, timeout: 60_000);
            output.WriteLine($"View mặc định: {d["n"]} bản ghi khớp tên");
            Assert.Equal("1", d["n"]);
            await d.Do(D365Action.ViewOpenRecord, "account", "", rowRef: name, timeout: 60_000);
            await d.Do(D365Action.GetRecordId, variable: "opened");
            Assert.Equal(d["accountId"], d["opened"]);

            // 5. Subgrid liên hệ (form account chuẩn có subgrid "Contacts"): + Mới, lưu, đếm dòng
            if (form.Subgrids.Any(g => g.Name == "Contacts"))
            {
                await d.Check(ConditionKind.D365SubgridCount, "Contacts", "0", wait: 15_000);
                await d.Do(D365Action.SubgridNew, "Contacts", timeout: 60_000);
                await d.Check(ConditionKind.D365FieldValue, "parentcustomerid", name, wait: 10_000);
                await d.Do(D365Action.SetField, "lastname", name + " LH");
                await d.Do(D365Action.Save, timeout: 60_000);
                await d.Do(D365Action.OpenForm, "account", d["accountId"], timeout: 60_000);
                await d.Check(ConditionKind.D365SubgridCount, "Contacts", "1", wait: 30_000);
                await d.Check(ConditionKind.D365SubgridRow, "Contacts", name + " LH", wait: 10_000);
                await d.Do(D365Action.SubgridOpenRow, "Contacts", rowRef: "1", timeout: 60_000);
                await d.Check(ConditionKind.D365FieldValue, "lastname", name + " LH");
            }
            else output.WriteLine("Form account không có subgrid \"Contacts\" — bỏ qua phần subgrid.");

            // 6. Tạo nhanh liên hệ có lookup điền sẵn (cần form tạo nhanh của contact được bật)
            await d.Do(D365Action.QuickCreate, $"lastname={name} QC\nparentcustomerid=account:{d["accountId"]}", "contact", "qcId", timeout: 60_000);
            await d.Check(ConditionKind.D365RecordCount, $"contacts?$select=lastname&$filter=contactid eq {d["qcId"]} and _parentcustomerid_value eq {d["accountId"]}", "1");
        }
        finally
        {
            // Dọn mọi bản ghi đã tạo (liên hệ trước, khách hàng sau)
            try
            {
                await d.Do(D365Action.Cleanup, timeout: 60_000);
            }
            catch (Exception ex)
            {
                output.WriteLine("Không dọn hết dữ liệu test: " + ex.Message + " — xóa tay các bản ghi tên \"ScheduleApp RealTest…\".");
            }
        }
    }
}

/// <summary>Chỉ chạy khi đặt SCHEDULEAPP_D365_URL (môi trường Dynamics 365 thật để kiểm thử).</summary>
public sealed class RealD365FactAttribute : FactAttribute
{
    public RealD365FactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SCHEDULEAPP_D365_URL")))
            Skip = "Cần môi trường Dynamics 365 thật — đặt SCHEDULEAPP_D365_URL (xem chú thích của D365RealTests).";
    }
}
