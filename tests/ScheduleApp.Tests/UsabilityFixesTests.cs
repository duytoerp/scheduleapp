using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.UI;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>
/// Các lỗi dễ dùng phát hiện khi thử app thật trong vai người dùng văn phòng (Chị Lan): tìm thao tác xếp sai, bước trống vẫn lưu,
/// đóng trình soạn mất flow không hỏi, nút Chạy thử bị che, hướng dẫn thiếu phần điều kiện, chữ ngày giờ khó đọc.
/// </summary>
public class UsabilityFixesTests
{
    private static void Sta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception(error.ToString());
    }

    private static T Field<T>(object o, string name) =>
        (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    private static JobEditorForm Editor(Job job, Size size)
    {
        var ui = new FakeUi();
        var f = new JobEditorForm(job, new FlowRunner(ui, _ => null), [job], ui, isNew: true)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = size
        };
        f.Show();
        Application.DoEvents();
        return f;
    }

    [Fact]
    public void SearchRanksAccentExactNameMatchFirst()
    {
        // "nhắc" (có dấu) phải ra "Hiện nhắc nhở" trước "Phát video / nhạc" (chỉ khớp khi bỏ dấu).
        Assert.True(StepVisuals.Score(StepType.Reminder, "Điều khiển luồng", "nhắc") > StepVisuals.Score(StepType.PlayMedia, "Ứng dụng", "nhắc"));
        Assert.True(StepVisuals.Score(StepType.PlayMedia, "Ứng dụng", "nhắc") >= 0);   // vẫn tìm thấy, chỉ xếp sau
        Assert.Equal(-1, StepVisuals.Score(StepType.MouseClick, "Chuột & bàn phím", "excel"));

        Sta(() =>
        {
            using var box = new StepToolbox();
            box.SetFilter("nhắc");
            Assert.Equal(StepType.Reminder, box.VisibleTypes[0]);
            Assert.Equal(StepType.Reminder, box.SelectedType);          // chọn sẵn mục khớp nhất → Enter là đúng mục
            Assert.Contains(StepType.PlayMedia, box.VisibleTypes);
            box.SetFilter("nhac");                                        // gõ không dấu: vẫn ra cả hai
            Assert.Contains(StepType.Reminder, box.VisibleTypes);
            box.SetFilter("click");
            Assert.Equal(StepType.MouseClick, box.VisibleTypes[0]);
            box.MoveSelection(1);
            Assert.Equal(box.VisibleTypes[1], box.SelectedType);
            box.SetFilter("");
            Assert.Equal(StepVisuals.Categories.Sum(c => c.Types.Length), box.VisibleTypes.Count);
        });
    }

    [Fact]
    public void PickerEnterAddsTheBestMatchForAccentedQuery()
    {
        Sta(() =>
        {
            using var owner = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(800, 600) };
            owner.Show();
            var picker = new NodePicker(owner, "Thêm bước");
            StepType? picked = null;
            picker.Picked += t => picked = t;
            picker.ShowAt(owner, new Point(-19900, -19900));
            Application.DoEvents();
            Assert.Equal("Thêm bước", picker.Text);
            picker.Search.Text = "nhắc";
            var keyDown = typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
            keyDown.Invoke(picker.Search, [new KeyEventArgs(Keys.Enter)]);
            Assert.Equal(StepType.Reminder, picked);
        });
    }

    [Fact]
    public void EmptyReminderAndLogStepsAreRejected()
    {
        Sta(() =>
        {
            string? Error(ActionStep step)
            {
                using var f = new StepEditorForm(step) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
                f.Show();
                Application.DoEvents();
                var build = typeof(StepEditorForm).GetMethod("BuildStep", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var built = (ActionStep)build.Invoke(f, null)!;
                var validate = typeof(StepEditorForm).GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(ActionStep)])!;
                var result = validate.Invoke(f, [built]);
                return result == null ? null : (string)result.GetType().GetField("Item1")!.GetValue(result)!;
            }

            Assert.Equal("Hãy nhập tiêu đề hoặc nội dung nhắc nhở.", Error(ActionStep.CreateDefault(StepType.Reminder)));
            Assert.Null(Error(S(StepType.Reminder, s => s.Target = "Họp giao ban 9h")));
            Assert.Null(Error(S(StepType.Reminder, s => s.Text = "Chuẩn bị báo cáo")));
            Assert.Equal("Hãy nhập nội dung cần ghi vào nhật ký.", Error(ActionStep.CreateDefault(StepType.LogMessage)));
            Assert.Null(Error(S(StepType.LogMessage, s => s.Text = "xong")));

            // Nhãn ô có ví dụ trong ngoặc: thông báo chỉ nêu tên ô ("Giá trị"), không lặp "(vd {{biến}})".
            var msg = Error(S(StepType.If, s => s.Condition = ConditionKind.Compare));
            Assert.NotNull(msg);
            Assert.DoesNotContain("(vd", msg);
        });
    }

    [Fact]
    public void ReminderNodeShowsTitleFirst()
    {
        var s = S(StepType.Reminder, x => { x.Target = "Họp giao ban 9h"; x.Text = "Chuẩn bị báo cáo tuần"; });
        Assert.StartsWith("Nhắc: Họp giao ban 9h", s.Describe());
        Assert.StartsWith("Nhắc: Chỉ nội dung", S(StepType.Reminder, x => x.Text = "Chỉ nội dung").Describe());
    }

    [Fact]
    public void EditorTracksUnsavedChangesAndKeepsRunButtonVisible()
    {
        Sta(() =>
        {
            var job = new Job { Name = "Nhắc họp sáng", Steps = [S(StepType.LogMessage, s => s.Text = "a")] };
            using var f = Editor(job, new Size(1200, 860));
            Assert.False(f.HasUnsavedChanges);

            // Sửa tên → có thay đổi; trả lại như cũ → hết.
            var name = Field<TextBox>(f, "_txtName");
            name.Text = "Tên khác";
            Assert.True(f.HasUnsavedChanges);
            name.Text = "Nhắc họp sáng";
            Assert.False(f.HasUnsavedChanges);

            // Thêm bước trên sơ đồ → có thay đổi.
            var designer = Field<FlowDesigner>(f, "_designer");
            designer.InsertStep(1, S(StepType.LogMessage, s => s.Text = "b"));
            Assert.True(f.HasUnsavedChanges);

            // Ở kích thước mặc định (1200×860) nút "▶ Chạy thử flow" nằm trọn trong vùng nhìn thấy của cột nút.
            var run = Field<Button>(f, "_btnTest");
            var column = run.Parent!;
            Assert.True(run.Visible);
            Assert.True(run.Bottom <= column.ClientSize.Height, $"nút Chạy thử bị che: bottom {run.Bottom} > {column.ClientSize.Height}");
            Assert.True(run.Top >= 0);

            // Nút Bắt đầu trên sơ đồ ghi lịch thật.
            Assert.Equal(job.Schedule.Describe(), designer.StartSubtitle);
        });
    }

    [Fact]
    public void CancelWithUnsavedChangesAsksFirst()
    {
        Sta(() =>
        {
            var job = new Job { Name = "Nhắc họp sáng" };
            var ui = new FakeUi();
            using var f = new JobEditorForm(job, new FlowRunner(ui, _ => null), [job], ui, isNew: true)
            {
                StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000)
            };
            var asked = new List<string>();
            bool stillOpenAfterCancel = false;
            f.Shown += (_, _) => f.BeginInvoke(new System.Windows.Forms.MethodInvoker(() =>
            {
                // Chưa sửa gì: Hủy đóng luôn, không hỏi → kiểm tra ở lần sau. Sửa tên → có thay đổi.
                Field<TextBox>(f, "_txtName").Text = "Nhắc họp sáng (sửa)";
                f.AskSaveChanges = _ => { asked.Add("cancel"); return DialogResult.Cancel; };
                f.CancelButton!.PerformClick();                 // Hủy / Esc → hỏi → "Hủy" = quay lại sửa tiếp
                // Hộp thoại modal chỉ đóng khi vòng lặp thông điệp kiểm tra → bước sau chạy bằng timer.
                var timer = new System.Windows.Forms.Timer { Interval = 300 };
                timer.Tick += (_, _) =>
                {
                    timer.Dispose();
                    stillOpenAfterCancel = f.Visible;
                    f.AskSaveChanges = _ => { asked.Add("no"); return DialogResult.No; };
                    f.CancelButton!.PerformClick();             // hỏi lại → "Không" = bỏ thay đổi, đóng
                };
                timer.Start();
            }));
            var result = f.ShowDialog();
            Assert.True(stillOpenAfterCancel);
            Assert.Equal(["cancel", "no"], asked);
            Assert.Equal(DialogResult.Cancel, result);
        });

        Sta(() =>
        {
            // Không có thay đổi: Hủy đóng ngay, không hỏi.
            var job = new Job { Name = "Không đổi" };
            var ui = new FakeUi();
            using var f = new JobEditorForm(job, new FlowRunner(ui, _ => null), [job], ui, isNew: false)
            {
                StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000)
            };
            int asked = 0;
            f.AskSaveChanges = _ => { asked++; return DialogResult.Cancel; };
            f.Shown += (_, _) => f.BeginInvoke(new System.Windows.Forms.MethodInvoker(() => f.CancelButton!.PerformClick()));
            Assert.Equal(DialogResult.Cancel, f.ShowDialog());
            Assert.Equal(0, asked);
        });
    }

    [Fact]
    public void HelpHasConditionsTopicReachableFromIfStepAndSearch()
    {
        var topic = HelpContent.Find("conditions");
        Assert.NotNull(topic);
        Assert.Contains("So với", topic!.Body);
        Assert.Contains("{{loop.index}}", topic.Body);
        Assert.Equal(topic.Title, topic.ToString());                       // tên mục danh sách đọc được (không phải chuỗi record)

        // Tìm "điều kiện": chủ đề điều kiện xếp đầu, trước các chủ đề D365.
        var query = "dieu kien";
        var ranked = HelpContent.Topics.Where(t => HelpView.Matches(t, query)).OrderByDescending(t => HelpView.Relevance(t, query)).ToList();
        Assert.Equal("conditions", ranked[0].Id);

        Sta(() =>
        {
            foreach (var type in new[] { StepType.If, StepType.Loop })
            {
                using var f = new StepEditorForm(ActionStep.CreateDefault(type));
                var id = (string)typeof(StepEditorForm).GetProperty("HelpTopicId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f)!;
                Assert.Equal("conditions", id);
            }
        });
    }

    [Fact]
    public void ConditionHelpExamplesProduceTheValuesTheyClaim()
    {
        // Ví dụ trong chủ đề "Điều kiện": {{today:dddd}} so với "Thứ Sáu", {{now:HH}} so với 12.
        var x = new Services.Engine.VariableExpander(new Dictionary<string, string>());
        Assert.True(Services.Engine.ConditionEvaluator.Compare(x.Expand("{{today:dddd}}"), CompareOp.Equals, ScheduleConfig.DayLongName(DateTime.Today.DayOfWeek)));
        Assert.Matches("^[0-2][0-9]$", x.Expand("{{now:HH}}"));
    }

    [Fact]
    public void ScheduleAndTimesReadNaturally()
    {
        var weekdays = new ScheduleConfig
        {
            Type = ScheduleType.Weekly, StartAt = new DateTime(2026, 10, 1, 8, 30, 0),
            Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]
        };
        Assert.Equal("T2–T6 lúc 08:30", weekdays.Describe());
        weekdays.Days = [DayOfWeek.Monday, DayOfWeek.Friday];
        Assert.Equal("T2, T6 lúc 08:30", weekdays.Describe());
        weekdays.Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Saturday, DayOfWeek.Sunday];
        Assert.Equal("T2–T4, T7, CN lúc 08:30", weekdays.Describe());

        var today = DateTime.Today;
        Assert.Equal("Hôm nay 08:30", MainForm.FriendlyTime(today.AddHours(8.5)));
        Assert.Equal("Ngày mai 07:05", MainForm.FriendlyTime(today.AddDays(1).AddHours(7).AddMinutes(5)));
        var later = today.AddDays(10).AddHours(9);
        Assert.Equal($"{ScheduleConfig.DayName(later.DayOfWeek)} {later:dd/MM} 09:00".Replace($"/{later:yyyy}", ""),
            MainForm.FriendlyTime(later).Replace($"/{later:yyyy}", ""));
    }

    [Fact]
    public void TemplatesListBasicsFirstWithoutPersonalNamesOrStaleDates()
    {
        var load = typeof(TemplatePickerForm).GetMethod("LoadTemplates", BindingFlags.NonPublic | BindingFlags.Static)!;
        var templates = (List<Job>)load.Invoke(null, null)!;
        Assert.StartsWith("01", templates[0].Name);
        int firstD365 = templates.FindIndex(t => t.Steps.Any(s => s.Type == StepType.Dynamics));
        int lastBasic = templates.FindLastIndex(t => t.Name.Length > 2 && char.IsDigit(t.Name[0]) && char.IsDigit(t.Name[1]));
        Assert.True(lastBasic < firstD365, "mẫu cơ bản phải đứng trước mẫu Dynamics 365");
        Assert.DoesNotContain(templates, t => t.Name.Contains("\"Tai\""));
        Assert.All(templates.Where(t => t.Schedule.Type == ScheduleType.Once), t => Assert.True(t.Schedule.StartAt > DateTime.Now, t.Name));
    }

    [Fact]
    public void SmallFlowStaysFullyVisibleAfterAddingSteps()
    {
        Sta(() =>
        {
            using var d = new FlowDesigner { Size = new Size(900, 450) };
            var steps = new List<ActionStep>();
            d.SetSteps(steps);
            d.ResetZoom();
            d.ZoomToFit(1f);
            for (int i = 0; i < 5; i++) d.InsertStep(steps.Count, S(StepType.LogMessage, s => s.Text = "b" + i));

            // 5 bước ở 100% rộng hơn khung 900 px → tự thu nhỏ (không dưới 75%) và cả sơ đồ, kể cả nút "+" cuối, nằm trong khung.
            var b = d.Graph.ContentBounds;
            Assert.InRange(d.Zoom, 0.75f, 0.999f);
            Assert.True(b.Left * d.Zoom + d.Pan.X >= -1 && b.Right * d.Zoom + d.Pan.X <= 901, "sơ đồ phải nằm trọn trong khung");
            var stub = d.Graph.StubBounds;
            Assert.True(stub.Right * d.Zoom + d.Pan.X <= 900);
        });
    }

    [Fact]
    public void EmptyBranchesAlwaysOfferPlus()
    {
        var steps = new List<ActionStep> { S(StepType.If), S(StepType.EndIf), S(StepType.Loop), S(StepType.EndLoop) };
        var layout = FlowGraphLayout.Build(steps, FlowStructure.Build(steps), FlowGraphLayout.Metrics.Scaled(v => v));
        var slots = layout.Edges.Where(e => e.IsSlot).ToList();
        Assert.Equal(3, slots.Count);                                   // nhánh đúng, nhánh sai, thân lặp
        Assert.Contains(slots, e => e.NeedsElse);
        Assert.Contains(slots, e => e.Kind == GraphEdgeKind.LoopBack);
        var filled = new List<ActionStep> { S(StepType.If), S(StepType.LogMessage), S(StepType.Else), S(StepType.LogMessage), S(StepType.EndIf) };
        var l2 = FlowGraphLayout.Build(filled, FlowStructure.Build(filled), FlowGraphLayout.Metrics.Scaled(v => v));
        Assert.DoesNotContain(l2.Edges, e => e.IsSlot);
    }
}
