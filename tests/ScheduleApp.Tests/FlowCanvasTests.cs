using System.Drawing;
using System.Windows.Forms;
using ScheduleApp.Models;
using ScheduleApp.UI;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>Sơ đồ flow kiểu n8n: bố cục nút / dây nối, chèn và di chuyển bước qua dây, thu phóng, tìm thao tác, vẽ ra ảnh.</summary>
public class FlowCanvasTests
{
    private static readonly FlowGraphLayout.Metrics M = FlowGraphLayout.Metrics.Scaled(v => v);

    private static ActionStep Log(string text) => S(StepType.LogMessage, s => s.Text = text);

    private static FlowGraphLayout Build(List<ActionStep> steps) => FlowGraphLayout.Build(steps, FlowStructure.Build(steps), M);

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
        if (error != null) throw new Exception(error.Message, error);
    }

    /// <summary>Flow mẫu: 0 Log · 1 Nếu · 2 Log a · 3 Không thì · 4 Log b · 5 Hết Nếu · 6 Lặp · 7 Log c · 8 Hết lặp · 9 Log cuối.</summary>
    private static List<ActionStep> Sample() =>
    [
        Log("đầu"),
        S(StepType.If),
        Log("a"),
        S(StepType.Else),
        Log("b"),
        S(StepType.EndIf),
        S(StepType.Loop, s => s.Count = 3),
        Log("c"),
        S(StepType.EndLoop),
        Log("cuối")
    ];

    private static GraphEdge EdgeAt(FlowGraphLayout l, int insertIndex, bool needsElse = false) =>
        l.Edges.Single(e => e.InsertIndex == insertIndex && e.NeedsElse == needsElse && e.Kind != GraphEdgeKind.Stub);

    [Fact]
    public void LinearFlowRunsLeftToRightWithOneEdgePerInsertPosition()
    {
        var l = Build([Log("1"), Log("2"), Log("3")]);
        var xs = Enumerable.Range(0, 3).Select(i => l.Bounds[i]!.Value.X).ToList();
        Assert.Equal(xs.Order(), xs);
        Assert.True(l.StartBounds.Right < xs[0]);
        Assert.All(Enumerable.Range(0, 3), i => Assert.Equal(0, l.CenterY(i)));
        Assert.Equal([0, 1, 2, 3], l.Edges.Select(e => e.InsertIndex).Order());
        var stub = l.Edges.Single(e => e.Kind == GraphEdgeKind.Stub);
        Assert.Equal(3, stub.InsertIndex);
        Assert.True(l.StubBounds.X > l.Bounds[2]!.Value.Right);
    }

    [Fact]
    public void EmptyFlowShowsLargeAddFirstStepBox()
    {
        var l = Build([]);
        var edge = Assert.Single(l.Edges);
        Assert.Equal((GraphEdgeKind.Stub, 0), (edge.Kind, edge.InsertIndex));
        Assert.Equal(M.Node, l.StubBounds.Width);
    }

    [Fact]
    public void IfSplitsIntoTrueAboveAndFalseBelowThenMerges()
    {
        var l = Build(Sample());
        float y = l.CenterY(1);
        Assert.True(l.CenterY(2) < y, "nhánh đúng ở trên");
        Assert.True(l.CenterY(4) > y, "nhánh sai ở dưới");
        Assert.Null(l.Bounds[3]);                                   // "Không thì" ẩn — chính là cổng "sai"
        Assert.True(l.IsMerge[5]);                                  // "Hết Nếu" = nút gộp tròn
        Assert.Equal(y, l.CenterY(5));
        Assert.True(l.Bounds[5]!.Value.X > Math.Max(l.Bounds[2]!.Value.Right, l.Bounds[4]!.Value.Right));

        // Các bước không chồng lên nhau (kể cả vùng chữ dưới nút).
        var boxes = Enumerable.Range(0, 10).Where(l.IsStepNode)
            .Select(i => { var b = l.Bounds[i]!.Value; return new RectangleF(b.X, b.Y, b.Width, b.Height + M.Label); }).ToList();
        for (int a = 0; a < boxes.Count; a++)
            for (int b = a + 1; b < boxes.Count; b++)
                Assert.False(boxes[a].IntersectsWith(boxes[b]), $"nút {a} và {b} chồng nhau");

        // Dây nối ↔ vị trí chèn: vào nhánh đúng (2), cuối nhánh đúng = trước "Không thì" (3),
        // đầu nhánh sai (4), cuối nhánh sai = trước "Hết Nếu" (5), sau khối Nếu (6).
        foreach (int index in new[] { 2, 3, 4, 5, 6 }) EdgeAt(l, index);
        Assert.DoesNotContain(l.Edges, e => e.NeedsElse);
    }

    [Fact]
    public void IfWithoutElseOffersFalseBranchThatCreatesElse()
    {
        var steps = new List<ActionStep> { S(StepType.If), Log("a"), S(StepType.EndIf) };
        var l = Build(steps);
        var falseEdge = EdgeAt(l, 2, needsElse: true);
        Assert.True(falseEdge.Mid.Y > l.CenterY(0));
        var trueEnd = EdgeAt(l, 2);
        Assert.True(trueEnd.Points.Min(p => p.Y) < l.CenterY(0));
    }

    [Fact]
    public void LoopBodySitsBelowWithEdgeBackToLoop()
    {
        var l = Build(Sample());
        Assert.True(l.CenterY(7) > l.CenterY(6) + M.Node, "thân lặp ở hàng dưới");
        Assert.Null(l.Bounds[8]);                                    // "Hết lặp" ẩn — thay bằng dây quay về
        var back = l.Edges.Single(e => e.Kind == GraphEdgeKind.LoopBack);
        Assert.Equal(8, back.InsertIndex);
        Assert.Equal(new PointF(l.Bounds[6]!.Value.X, l.CenterY(6)), back.Points[^1]);   // về cổng vào của nút Lặp
        Assert.True(back.Mid.Y > l.Bounds[7]!.Value.Bottom);
        Assert.True(l.Bounds[9]!.Value.X > l.Bounds[7]!.Value.Right);                    // bước sau vòng lặp ở bên phải thân lặp
        Assert.Equal(l.CenterY(6), l.CenterY(9));
        EdgeAt(l, 9);                                                                     // dây "xong" → bước sau
    }

    [Fact]
    public void EmptyLoopBodyStillHasPlusSlot()
    {
        var l = Build([S(StepType.Loop), S(StepType.EndLoop)]);
        var back = l.Edges.Single(e => e.Kind == GraphEdgeKind.LoopBack);
        Assert.Equal(1, back.InsertIndex);
        Assert.True(back.Mid.X > l.Bounds[0]!.Value.Right);
    }

    [Fact]
    public void BrokenStructureShowsMarkersAsNormalNodes()
    {
        var l = Build([Log("a"), S(StepType.EndIf), S(StepType.Else), S(StepType.If)]);
        Assert.All(Enumerable.Range(0, 4), i => Assert.True(l.IsStepNode(i)));
        Assert.Equal([0, 1, 2, 3, 4], l.Edges.Select(e => e.InsertIndex).Order());
    }

    [Fact]
    public void GotoAndErrorLabelDrawJumpLinks()
    {
        var l = Build(
        [
            S(StepType.Label, s => s.Target = "lại"),
            Log("x"),
            S(StepType.Wait, s => { s.OnError = ErrorAction.GotoLabel; s.ErrorLabel = "lại"; }),
            S(StepType.Goto, s => s.Target = "lại")
        ]);
        Assert.Equal([new GraphJump(2, 0, true), new GraphJump(3, 0, false)], l.Jumps);
        Assert.True(l.ContentBounds.Top <= l.JumpTop(l.Jumps[1]));
    }

    [Fact]
    public void NestedBlocksLayOutWithoutOverlap()
    {
        var steps = new List<ActionStep>
        {
            S(StepType.Loop), S(StepType.If), Log("a"), S(StepType.Else), S(StepType.If), Log("b"), S(StepType.EndIf), S(StepType.EndIf), S(StepType.EndLoop), Log("z")
        };
        var l = Build(steps);
        Assert.True(FlowStructure.Build(steps).IsValid);
        var nodes = Enumerable.Range(0, steps.Count).Where(l.IsVisible).Select(i => l.Bounds[i]!.Value).ToList();
        for (int a = 0; a < nodes.Count; a++)
            for (int b = a + 1; b < nodes.Count; b++)
                Assert.False(nodes[a].IntersectsWith(nodes[b]));
        var area = l.ContentBounds;
        area.Inflate(1, 1);
        Assert.All(l.Edges.SelectMany(e => e.Points), p => Assert.True(area.Contains(p), $"điểm {p} nằm ngoài vùng sơ đồ"));
        // Mọi vị trí chèn 0..Count đều có dây để thả vào; "Hết Nếu" bên trong (6) có thêm dây nhánh sai tự tạo "Không thì".
        Assert.Equal(Enumerable.Range(0, steps.Count + 1), l.Edges.Where(e => !e.NeedsElse).Select(e => e.InsertIndex).Distinct().Order());
        Assert.Equal(6, l.Edges.Single(e => e.NeedsElse).InsertIndex);
    }

    [Fact]
    public void DesignerMovesStepIntoFalseBranchCreatingElse()
    {
        Sta(() =>
        {
            var steps = new List<ActionStep> { Log("x"), S(StepType.If), Log("a"), S(StepType.EndIf) };
            using var d = new FlowDesigner { Size = new Size(1200, 600) };
            d.SetSteps(steps);
            var falseEdge = d.Graph.Edges.Single(e => e.NeedsElse);
            Assert.True(d.MoveToEdge(0, falseEdge));
            Assert.Equal([StepType.If, StepType.LogMessage, StepType.Else, StepType.LogMessage, StepType.EndIf], steps.Select(s => s.Type));
            Assert.Equal("x", steps[3].Text);
            Assert.True(d.Structure.IsValid);
            Assert.Equal(3, d.SelectedIndex);
        });
    }

    [Fact]
    public void DesignerMovesWholeBlockAndRefusesDropIntoItself()
    {
        Sta(() =>
        {
            var steps = Sample();
            using var d = new FlowDesigner { Size = new Size(1200, 600) };
            d.SetSteps(steps);
            int changes = 0;
            d.StepsChanged += (_, _) => changes++;

            // Dây bên trong khối Nếu không bao giờ là chỗ thả khi đang kéo chính khối đó.
            var inside = d.Graph.Edges.FindIndex(e => e.InsertIndex == 4);
            int nearest = d.NearestEdge(d.Graph.Edges[inside].Mid, dragging: 1);
            Assert.NotEqual(inside, nearest);
            Assert.False(d.MoveToEdge(1, d.Graph.Edges[inside]));
            Assert.False(d.MoveToEdge(1, d.Graph.Edges.Single(e => e.InsertIndex == 1)));   // đúng chỗ cũ
            Assert.Equal(0, changes);

            // Kéo khối Nếu (5 bước) vào cuối flow.
            Assert.True(d.MoveToEdge(1, d.Graph.Edges.Single(e => e.Kind == GraphEdgeKind.Stub)));
            Assert.Equal(["đầu", "", "c", "", "cuối", "", "a", "", "b", ""],
                steps.Select(s => s.Type == StepType.LogMessage ? s.Text : ""));
            Assert.Equal(StepType.If, steps[5].Type);
            Assert.True(d.Structure.IsValid);
            Assert.Equal(1, changes);

            // Kéo bước vào thân vòng lặp (dây quay về = cuối thân lặp).
            Assert.True(d.MoveToEdge(0, d.Graph.Edges.Single(e => e.Kind == GraphEdgeKind.LoopBack)));
            Assert.Equal([StepType.Loop, StepType.LogMessage, StepType.LogMessage, StepType.EndLoop], steps.Take(4).Select(s => s.Type));
            Assert.Equal("đầu", steps[2].Text);
        });
    }

    [Fact]
    public void AddingToFalseBranchKeepsElseOnlyWhenStepWasAdded()
    {
        Sta(() =>
        {
            var steps = new List<ActionStep> { S(StepType.If), Log("a"), S(StepType.EndIf) };
            using var d = new FlowDesigner { Size = new Size(1200, 600) };
            d.SetSteps(steps);
            var requests = new List<(StepType, int)>();
            bool accept = false;
            d.AddRequested += (type, index) =>
            {
                requests.Add((type, index));
                if (accept) d.InsertStep(index, S(type, s => s.Text = "mới"));
            };

            // Người dùng hủy trình soạn → không để lại "Không thì" thừa.
            d.AddViaEdge(StepType.LogMessage, d.Graph.Edges.Single(e => e.NeedsElse));
            Assert.Equal([(StepType.LogMessage, 3)], requests);
            Assert.Equal([StepType.If, StepType.LogMessage, StepType.EndIf], steps.Select(s => s.Type));

            accept = true;
            d.AddViaEdge(StepType.LogMessage, d.Graph.Edges.Single(e => e.NeedsElse));
            Assert.Equal([StepType.If, StepType.LogMessage, StepType.Else, StepType.LogMessage, StepType.EndIf], steps.Select(s => s.Type));
            Assert.Equal("mới", steps[3].Text);
            Assert.True(d.Structure.IsValid);

            // Thêm vào nhánh đúng: chèn trước "Không thì".
            d.AddViaEdge(StepType.Wait, d.Graph.Edges.Single(e => e.InsertIndex == 2 && !e.NeedsElse));
            Assert.Equal((StepType.Wait, 2), requests[^1]);
            Assert.Equal(StepType.Wait, steps[2].Type);
        });
    }

    [Fact]
    public void ZoomKeepsPointUnderMouseAndFitShowsWholeFlow()
    {
        Sta(() =>
        {
            var steps = Sample();
            using var d = new FlowDesigner { Size = new Size(1400, 700) };
            d.SetSteps(steps);
            d.ZoomToFit();
            var b = d.Graph.ContentBounds;
            Assert.InRange(d.Zoom, 0.5f, 1f);
            Assert.True(b.X * d.Zoom + d.Pan.X >= -1 && b.Right * d.Zoom + d.Pan.X <= 1401, "vừa ngang khung");

            var anchor = new Point(500, 300);
            var before = new PointF((anchor.X - d.Pan.X) / d.Zoom, (anchor.Y - d.Pan.Y) / d.Zoom);
            d.SetZoom(d.Zoom * 1.5f, anchor);
            var after = new PointF((anchor.X - d.Pan.X) / d.Zoom, (anchor.Y - d.Pan.Y) / d.Zoom);
            Assert.Equal(before.X, after.X, 2);
            Assert.Equal(before.Y, after.Y, 2);

            d.SetZoom(10, anchor);
            Assert.Equal(2f, d.Zoom);
            d.SetZoom(0.01f, anchor);
            Assert.Equal(0.25f, d.Zoom);
            d.ResetZoom();
            Assert.Equal(1f, d.Zoom);
        });
    }

    [Fact]
    public void ToolboxSearchIgnoresVietnameseAccents()
    {
        Assert.Equal("nhap chu", StepVisuals.Fold("Nhập chữ"));
        Assert.Equal("dieu kien", StepVisuals.Fold("Điều kiện"));
        Assert.True(StepVisuals.Matches(StepType.Dynamics, "Kiểm thử & Dynamics 365", "dynamics 365"));
        Assert.True(StepVisuals.Matches(StepType.LogMessage, "Biến & dữ liệu", "nhat ky"));
        Assert.True(StepVisuals.Matches(StepType.If, "Điều kiện & lặp", "dieu kien"));
        Assert.False(StepVisuals.Matches(StepType.MouseClick, "Chuột & bàn phím", "excel"));

        Sta(() =>
        {
            using var box = new StepToolbox();
            int all = box.VisibleTypes.Count;
            Assert.Equal(StepVisuals.Categories.Sum(c => c.Types.Length), all);
            box.SetFilter("dynamics");
            Assert.Contains(StepType.Dynamics, box.VisibleTypes);
            Assert.True(box.VisibleTypes.Count < all);
            box.SetFilter("nếu");
            Assert.Contains(StepType.If, box.VisibleTypes);
            box.SetFilter("zzzz");
            Assert.Empty(box.VisibleTypes);
            Assert.Empty(box.Items);                     // nhóm trống cũng ẩn
            box.SetFilter("");
            Assert.Equal(all, box.VisibleTypes.Count);
        });
    }

    [Fact]
    public void NodePickerPicksFirstMatchOnEnter()
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
            picker.Search.Text = "ghi nhat ky";
            Assert.Equal(StepType.LogMessage, picker.List.VisibleTypes[0]);
            var keyDown = typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            keyDown.Invoke(picker.Search, [new KeyEventArgs(Keys.Enter)]);
            Application.DoEvents();
            Assert.Equal(StepType.LogMessage, picked);
            Assert.True(picker.IsDisposed);
        });
    }

    [Fact]
    public void RendersNodesEdgesAndRunStateToBitmap()
    {
        Sta(() =>
        {
            var steps = Sample();
            steps[4].Enabled = false;
            steps[7].Breakpoint = true;
            using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(1500, 760) };
            var d = new FlowDesigner { Dock = DockStyle.Fill };
            form.Controls.Add(d);
            form.Show();
            d.SetSteps(steps);
            d.SelectStep(2);
            d.SetRunning(0);
            d.SetRunning(7);
            Application.DoEvents();

            using var bmp = new Bitmap(d.ClientSize.Width, d.ClientSize.Height);
            d.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
            var dir = Environment.GetEnvironmentVariable("SCHEDULEAPP_SNAPSHOT_DIR");
            if (!string.IsNullOrEmpty(dir)) bmp.Save(Path.Combine(dir, "flow-canvas.png"));

            Point Screen(PointF p) => Point.Round(new PointF(p.X * d.Zoom + d.Pan.X, p.Y * d.Zoom + d.Pan.Y));
            Color canvas = Color.FromArgb(246, 247, 249);
            // Nút: nền trắng (góc phải dưới, tránh số thứ tự và đường gạch chéo); nút đã tắt: nền xám.
            Color Inside(int i)
            {
                var b = d.Graph.Bounds[i]!.Value;
                var p = Screen(new PointF(b.Right - 12, b.Bottom - 12));
                return bmp.GetPixel(p.X, p.Y);
            }
            Assert.Equal(Color.White.ToArgb(), Inside(2).ToArgb());
            Assert.Equal(Color.FromArgb(243, 243, 244).ToArgb(), Inside(4).ToArgb());
            // Dây nối: điểm giữa dây đầu tiên không còn màu nền.
            var mid = Screen(d.Graph.Edges.First(e => e.InsertIndex == 0).Mid);
            Assert.NotEqual(canvas.ToArgb(), bmp.GetPixel(mid.X, mid.Y).ToArgb());
            // Bước 1 đã chạy xong → viền xanh lá; bước 8 đang chạy → viền xanh dương.
            var doneBox = d.Graph.Bounds[0]!.Value;
            var edgePx = Screen(new PointF(doneBox.X + doneBox.Width / 2, doneBox.Y));
            var c = bmp.GetPixel(edgePx.X, edgePx.Y);
            Assert.True(c.G > c.R + 40 && c.G > c.B, $"viền bước đã chạy phải xanh lá, thấy {c}");
        });
    }
}
