using System.Drawing;
using System.Reflection;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.UI;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>Cách nhìn "Danh sách" của flow: thụt lề theo khối, thu gọn / mở khối, chèn khi thả, dùng chung vùng chọn với sơ đồ.</summary>
public class FlowListViewTests
{
    /// <summary>1 Mở · 2 Nếu [3 Gõ · 4 Không thì · 5 Ghi] 6 Hết Nếu · 7 Lặp [8 Click · 9 Chờ] 10 Hết lặp · 11 Gửi.</summary>
    private static List<ActionStep> Flow() =>
    [
        S(StepType.LaunchApp, s => s.Target = "excel.exe"),
        S(StepType.If),
        S(StepType.TypeText, s => s.Text = "a"),
        S(StepType.Else),
        S(StepType.LogMessage, s => s.Text = "b"),
        S(StepType.EndIf),
        S(StepType.Loop),
        S(StepType.MouseClick),
        S(StepType.Wait),
        S(StepType.EndLoop),
        S(StepType.Notify, s => s.Text = "xong")
    ];

    private static void OnSta(Action action)
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
        if (error != null) throw error;
    }

    [Fact]
    public void CollapsedBlocksHideTheirInnerSteps()
    {
        var steps = Flow();
        var fs = FlowStructure.Build(steps);
        Assert.Equal(Enumerable.Range(0, 11), FlowListView.VisibleRows(steps, fs, _ => false));
        // Thu khối Nếu: ẩn cả nhánh đúng, "Không thì", nhánh sai và "Hết Nếu".
        Assert.Equal([0, 1, 6, 7, 8, 9, 10], FlowListView.VisibleRows(steps, fs, s => ReferenceEquals(s, steps[1])));
        Assert.Equal([0, 1, 6, 10], FlowListView.VisibleRows(steps, fs, s => s.Type is StepType.If or StepType.Loop));
        Assert.Equal(2, FlowListView.InnerCount(steps, fs, 1));   // Gõ + Ghi (không tính "Không thì")
        Assert.Equal(2, FlowListView.InnerCount(steps, fs, 6));
    }

    [Fact]
    public void ListSharesSelectionAndCollapsesWithKeys()
    {
        OnSta(() =>
        {
            using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(800, 600) };
            var designer = new FlowDesigner { Dock = DockStyle.Fill, Visible = false };
            var list = new FlowListView(designer) { Dock = DockStyle.Fill };
            form.Controls.AddRange([designer, list]);
            var steps = Flow();
            designer.SetSteps(steps);
            form.Show();
            Application.DoEvents();

            // Thu khối Lặp khi đang chọn bước bên trong → chọn đầu khối; thả sau khối thu gọn → chèn sau cả khối.
            designer.SelectStep(8);
            list.ToggleBlock(6, expand: false);
            Assert.Equal(6, designer.SelectedIndex);
            Assert.Equal([0, 1, 2, 3, 4, 5, 6, 10], list.Rows);
            Assert.Equal(10, list.InsertIndexFor(6, after: true));
            Assert.Equal(6, list.InsertIndexFor(6, after: false));
            Assert.Equal(11, list.InsertIndexFor(-1, after: true));

            // Bước lỗi nằm trong khối đang thu → tự mở khối để thấy.
            designer.SetFailed(7);
            Assert.Contains(7, list.Rows);

            // Phím: ↑↓ đi theo dòng đang hiện, ← thu khối / về đầu khối, → mở, Delete xóa qua sơ đồ (dùng chung danh sách bước).
            var key = typeof(FlowListView).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
            void Press(Keys k) => key.Invoke(list, [new KeyEventArgs(k)]);
            designer.SelectStep(1);
            Press(Keys.Left);
            Assert.Equal([0, 1, 6, 7, 8, 9, 10], list.Rows);
            Press(Keys.Down);
            Assert.Equal(6, designer.SelectedIndex);                  // nhảy qua khối Nếu đang thu
            Press(Keys.Down);
            Press(Keys.Left);                                         // ở bước trong Lặp → về đầu khối Lặp
            Assert.Equal(6, designer.SelectedIndex);
            designer.SelectStep(1);
            Press(Keys.Right);
            Assert.Equal(Enumerable.Range(0, 11), list.Rows);
            designer.SelectStep(10);
            Press(Keys.Delete);
            Assert.Equal(10, steps.Count);
            Assert.Equal(Enumerable.Range(0, 10), list.Rows);

            int edited = -1;
            designer.EditRequested += i => edited = i;
            designer.SelectStep(2);
            Press(Keys.Enter);
            Assert.Equal(2, edited);
        });
    }

    [Fact]
    public void EditorRemembersTheChosenView()
    {
        OnSta(() =>
        {
            var saved = SettingsStore.Current.FlowView;
            try
            {
                SettingsStore.Current.FlowView = "list";
                var job = new Job { Name = "x", Steps = Flow() };
                using var f = new JobEditorForm(job, new FlowRunner(new FakeUi(), _ => null), [job], new FakeUi(), false)
                    { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
                f.Show();
                Application.DoEvents();
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var designer = (FlowDesigner)typeof(JobEditorForm).GetField("_designer", flags)!.GetValue(f)!;
                var list = (FlowListView)typeof(JobEditorForm).GetField("_flowList", flags)!.GetValue(f)!;
                var graphButton = (RadioButton)typeof(JobEditorForm).GetField("_viewGraph", flags)!.GetValue(f)!;
                Assert.True(list.Visible);
                Assert.False(designer.Visible);

                designer.SelectStep(4);
                graphButton.Checked = true;
                Assert.True(designer.Visible);
                Assert.False(list.Visible);
                Assert.Equal(4, designer.SelectedIndex);              // chuyển cách nhìn vẫn giữ bước đang chọn
                Assert.Equal("graph", SettingsStore.Current.FlowView);
            }
            finally { SettingsStore.Current.FlowView = saved; }
        });
    }
}
