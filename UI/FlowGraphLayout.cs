using ScheduleApp.Models;

namespace ScheduleApp.UI;

internal enum GraphEdgeKind
{
    /// <summary>Dây nối thường (trái → phải).</summary>
    Forward,

    /// <summary>Dây quay lại đầu vòng lặp (vòng xuống dưới thân lặp rồi về cổng vào của nút Lặp).</summary>
    LoopBack,

    /// <summary>Dây cuối flow, dẫn tới nút "+" để thêm bước tiếp theo.</summary>
    Stub
}

/// <summary>
/// Một dây nối trên sơ đồ. Mỗi dây ứng với một vị trí chèn trong danh sách bước phẳng:
/// thả / thêm bước vào dây này nghĩa là chèn vào <see cref="InsertIndex"/>.
/// </summary>
internal sealed class GraphEdge
{
    public GraphEdgeKind Kind { get; init; }

    /// <summary>Vị trí chèn (0..Count) trong danh sách bước.</summary>
    public int InsertIndex { get; init; }

    /// <summary>Nhánh "sai" của khối Nếu chưa có "Không thì": chèn vào đây phải tạo "Không thì" trước.</summary>
    public bool NeedsElse { get; init; }

    /// <summary>Các điểm của dây (tọa độ sơ đồ) — dùng để vẽ và dò chuột.</summary>
    public PointF[] Points { get; init; } = [];

    /// <summary>Chỗ đặt nút "+" trên dây.</summary>
    public PointF Mid { get; init; }

    /// <summary>Dây đi qua ô trống của nhánh / thân lặp chưa có bước — nút "+" luôn hiện ở <see cref="Mid"/>.</summary>
    public bool IsSlot { get; init; }
}

/// <summary>Dây nét đứt từ bước "Nhảy tới nhãn" (hoặc bước có "khi lỗi nhảy tới nhãn") tới bước Nhãn.</summary>
internal readonly record struct GraphJump(int From, int To, bool OnError);

/// <summary>
/// Bố cục sơ đồ kiểu n8n cho flow phẳng: các bước nối từ trái sang phải; khối Nếu tách hai nhánh đúng (trên) / sai (dưới)
/// rồi gộp lại ở nút tròn "Hết Nếu"; khối Lặp đặt thân lặp ở hàng dưới và có dây quay về nút Lặp.
/// Vị trí do bố cục tự tính từ thứ tự bước — thứ tự chạy vẫn là danh sách bước như cũ.
/// </summary>
internal sealed class FlowGraphLayout
{
    /// <summary>Kích thước (đơn vị điểm ảnh ở mức thu phóng 100%).</summary>
    public sealed record Metrics(float Node, float Label, float HGap, float VGap, float Merge, float Slot)
    {
        public static Metrics Scaled(Func<int, int> s) => new(s(96), s(78), s(86), s(44), s(26), s(40));

        /// <summary>Bề rộng vùng chữ dưới nút.</summary>
        public float LabelWidth => Node + HGap * 0.9f;
    }

    public const string TruePort = "đúng";
    public const string FalsePort = "sai";
    public const string LoopPort = "lặp";
    public const string DonePort = "xong";

    private readonly IReadOnlyList<ActionStep> _steps;
    private readonly FlowStructure _fs;
    private readonly Metrics _m;

    /// <summary>Khung nút của từng bước (null = bước đánh dấu được ẩn: "Không thì", "Hết lặp").</summary>
    public RectangleF?[] Bounds { get; }

    /// <summary>Bước "Hết Nếu" được vẽ thành nút gộp tròn nhỏ.</summary>
    public bool[] IsMerge { get; }

    public RectangleF StartBounds { get; private set; }

    /// <summary>Nút "+" cuối flow (khi flow trống: ô lớn "Thêm bước đầu tiên").</summary>
    public RectangleF StubBounds { get; private set; }

    public List<GraphEdge> Edges { get; } = [];

    public List<GraphJump> Jumps { get; } = [];

    /// <summary>Vùng bao toàn bộ sơ đồ (kể cả chữ dưới nút).</summary>
    public RectangleF ContentBounds { get; private set; }

    public Metrics Size => _m;

    private FlowGraphLayout(IReadOnlyList<ActionStep> steps, FlowStructure fs, Metrics m)
    {
        _steps = steps;
        _fs = fs;
        _m = m;
        Bounds = new RectangleF?[steps.Count];
        IsMerge = new bool[steps.Count];
    }

    public static FlowGraphLayout Build(IReadOnlyList<ActionStep> steps, FlowStructure fs, Metrics m)
    {
        var layout = new FlowGraphLayout(steps, fs, m);
        layout.Run();
        return layout;
    }

    /// <summary>Bước <paramref name="i"/> là đầu khối Nếu / Lặp hợp lệ (có bước kết thúc tương ứng).</summary>
    public bool IsIfBlock(int i) => _steps[i].Type == StepType.If && _fs.Match[i] > i;

    public bool IsLoopBlock(int i) => _steps[i].Type == StepType.Loop && _fs.Match[i] > i;

    public bool IsVisible(int i) => i >= 0 && i < Bounds.Length && Bounds[i] != null;

    /// <summary>Nút có thể kéo đi / có thanh công cụ (không phải nút gộp hay bước ẩn).</summary>
    public bool IsStepNode(int i) => IsVisible(i) && !IsMerge[i];

    /// <summary>Tâm hàng (trục ngang) của nút.</summary>
    public float CenterY(int i) => Bounds[i] is { } r ? r.Y + r.Height / 2 : 0;

    private float NodeUp => _m.Node / 2;
    private float NodeDown => _m.Node / 2 + _m.Label;

    private int Next(int i) => IsIfBlock(i) || IsLoopBlock(i) ? _fs.Match[i] + 1 : i + 1;

    private readonly record struct Extent(float W, float Up, float Down);

    private Extent MeasureSeq(int a, int b)
    {
        if (a >= b) return new Extent(_m.Slot, _m.Slot / 2, _m.Slot / 2);
        float w = 0, up = 0, down = 0;
        for (int i = a; i < b; i = Next(i))
        {
            var e = MeasureItem(i);
            w += (i == a ? 0 : _m.HGap) + e.W;
            up = Math.Max(up, e.Up);
            down = Math.Max(down, e.Down);
        }
        return new Extent(w, up, down);
    }

    /// <summary>Khoảng cách từ trục của nút Nếu tới trục nhánh đúng / nhánh sai.</summary>
    private (Extent T, Extent F, float Dt, float Df) IfBranches(int i)
    {
        int e = _fs.ElseOf[i], end = _fs.Match[i];
        var t = MeasureSeq(i + 1, e >= 0 ? e : end);
        var f = e >= 0 ? MeasureSeq(e + 1, end) : MeasureSeq(end, end);
        float dt = Math.Max(t.Down + _m.VGap / 2, _m.Node * 0.75f);
        float df = Math.Max(f.Up + _m.VGap / 2, _m.Node * 0.75f);
        return (t, f, dt, df);
    }

    private float LoopBodyOffset(Extent body) => NodeDown + _m.VGap + body.Up;

    private Extent MeasureItem(int i)
    {
        if (IsIfBlock(i))
        {
            var (t, f, dt, df) = IfBranches(i);
            float w = _m.Node + _m.HGap + Math.Max(t.W, f.W) + _m.HGap + _m.Merge;
            return new Extent(w, Math.Max(NodeUp, dt + t.Up), Math.Max(NodeDown, df + f.Down));
        }
        if (IsLoopBlock(i))
        {
            var body = MeasureSeq(i + 1, _fs.Match[i]);
            float w = _m.Node + _m.HGap + body.W + _m.HGap / 2;
            return new Extent(w, NodeUp, LoopBodyOffset(body) + body.Down + _m.VGap);
        }
        return new Extent(_m.Node, NodeUp, NodeDown);
    }

    private void Run()
    {
        float n = _m.Node;
        StartBounds = new RectangleF(0, -n / 2, n, n);
        var from = new PointF(n, 0);
        var x = n + _m.HGap;

        if (_steps.Count == 0)
        {
            StubBounds = new RectangleF(x, -n / 2, n, n);
            AddForward(from, new PointF(x, 0), 0, GraphEdgeKind.Stub);
        }
        else
        {
            var last = PlaceSeq(0, _steps.Count, x, 0, from);
            float s = _m.Merge;
            var stubX = last.X + _m.HGap * 0.6f;
            StubBounds = new RectangleF(stubX, last.Y - s / 2, s, s);
            AddForward(last, new PointF(stubX, last.Y), _steps.Count, GraphEdgeKind.Stub);
        }

        BuildJumps();
        ContentBounds = ComputeContentBounds();
    }

    /// <summary>Đặt các bước [a, b) trên trục <paramref name="y"/>; trả về cổng ra của bước cuối (hoặc <paramref name="from"/> nếu rỗng).</summary>
    private PointF PlaceSeq(int a, int b, float x, float y, PointF from)
    {
        var cur = from;
        for (int i = a; i < b; i = Next(i))
        {
            var ext = MeasureItem(i);
            var (inPt, outPt) = PlaceItem(i, x, y);
            AddForward(cur, inPt, i);
            cur = outPt;
            x += ext.W + _m.HGap;
        }
        return cur;
    }

    private (PointF In, PointF Out) PlaceItem(int i, float x, float y)
    {
        float n = _m.Node;
        Bounds[i] = new RectangleF(x, y - n / 2, n, n);
        var inPt = new PointF(x, y);

        if (IsIfBlock(i))
        {
            int e = _fs.ElseOf[i], end = _fs.Match[i];
            var (t, f, dt, df) = IfBranches(i);
            var truePort = new PointF(x + n, y - n / 4);
            var falsePort = new PointF(x + n, y + n / 4);
            float bx = x + n + _m.HGap;
            float mergeX = bx + Math.Max(t.W, f.W) + _m.HGap;
            Bounds[end] = new RectangleF(mergeX, y - _m.Merge / 2, _m.Merge, _m.Merge);
            IsMerge[end] = true;
            var mergeIn = new PointF(mergeX, y);

            int trueEnd = e >= 0 ? e : end;
            var tOut = PlaceSeq(i + 1, trueEnd, bx, y - dt, truePort);
            AddBranchEnd(tOut, truePort, mergeIn, trueEnd, empty: trueEnd == i + 1, slot: new PointF(bx + _m.Slot / 2, y - dt), needsElse: false);

            if (e >= 0)
            {
                var fOut = PlaceSeq(e + 1, end, bx, y + df, falsePort);
                AddBranchEnd(fOut, falsePort, mergeIn, end, empty: end == e + 1, slot: new PointF(bx + _m.Slot / 2, y + df), needsElse: false);
            }
            else
            {
                AddBranchEnd(falsePort, falsePort, mergeIn, end, empty: true, slot: new PointF(bx + _m.Slot / 2, y + df), needsElse: true);
            }
            return (inPt, new PointF(mergeX + _m.Merge, y));
        }

        if (IsLoopBlock(i))
        {
            int end = _fs.Match[i];
            var body = MeasureSeq(i + 1, end);
            var donePort = new PointF(x + n, y - n / 4);
            var loopPort = new PointF(x + n, y + n / 4);
            float by = y + LoopBodyOffset(body);
            float bx = x + n + _m.HGap;
            var bOut = PlaceSeq(i + 1, end, bx, by, loopPort);
            float bottom = by + body.Down + _m.VGap / 2;
            float right = Math.Max(bOut.X, bx + _m.Slot) + _m.HGap / 3;
            float left = x - _m.HGap / 3;

            if (end == i + 1)
            {
                // Thân lặp trống: lặp → ô "+" → quay về.
                var slot = new PointF(bx + _m.Slot / 2, by);
                var pts = Bezier(loopPort, slot).Concat(LoopBackPoints(slot, inPt, right, bottom, left).Skip(1)).ToArray();
                Edges.Add(new GraphEdge { Kind = GraphEdgeKind.LoopBack, InsertIndex = end, Points = pts, Mid = slot, IsSlot = true });
            }
            else
            {
                var pts = LoopBackPoints(bOut, inPt, right, bottom, left);
                Edges.Add(new GraphEdge { Kind = GraphEdgeKind.LoopBack, InsertIndex = end, Points = pts, Mid = new PointF((right + left) / 2, bottom) });
            }
            return (inPt, donePort);
        }

        return (inPt, new PointF(x + n, y));
    }

    /// <summary>Dây từ cuối nhánh tới nút gộp; nhánh trống thì dây đi qua ô "+" trên hàng của nhánh.</summary>
    private void AddBranchEnd(PointF branchOut, PointF port, PointF mergeIn, int insertIndex, bool empty, PointF slot, bool needsElse)
    {
        if (!empty)
        {
            AddForward(branchOut, mergeIn, insertIndex, needsElse: needsElse);
            return;
        }
        var pts = Bezier(port, slot).Concat(Bezier(slot, mergeIn).Skip(1)).ToArray();
        Edges.Add(new GraphEdge { Kind = GraphEdgeKind.Forward, InsertIndex = insertIndex, NeedsElse = needsElse, Points = pts, Mid = slot, IsSlot = true });
    }

    private void AddForward(PointF from, PointF to, int insertIndex, GraphEdgeKind kind = GraphEdgeKind.Forward, bool needsElse = false)
    {
        var pts = Bezier(from, to);
        Edges.Add(new GraphEdge
        {
            Kind = kind,
            InsertIndex = insertIndex,
            NeedsElse = needsElse,
            Points = pts,
            Mid = kind == GraphEdgeKind.Stub ? to : pts[pts.Length / 2]
        });
    }

    /// <summary>Đường cong Bézier nằm ngang giữa hai cổng (như dây nối của n8n), lấy mẫu thành các điểm.</summary>
    public static PointF[] Bezier(PointF a, PointF b, int samples = 24)
    {
        if (Math.Abs(a.Y - b.Y) < 0.5f)
            return [a, new PointF((a.X + b.X) / 2, a.Y), b];
        float dx = Math.Max(Math.Abs(b.X - a.X) / 2, 30);
        var c1 = new PointF(a.X + dx, a.Y);
        var c2 = new PointF(b.X - dx, b.Y);
        var pts = new PointF[samples + 1];
        for (int k = 0; k <= samples; k++)
        {
            float t = k / (float)samples, u = 1 - t;
            pts[k] = new PointF(
                u * u * u * a.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * b.X,
                u * u * u * a.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * b.Y);
        }
        return pts;
    }

    /// <summary>Dây quay về: sang phải → xuống dưới thân lặp → sang trái → lên → vào cổng vào của nút Lặp (bo góc).</summary>
    private PointF[] LoopBackPoints(PointF from, PointF to, float right, float bottom, float left)
    {
        float r = Math.Min(_m.VGap / 2, 16);
        return RoundedPolyline([from, new PointF(right, from.Y), new PointF(right, bottom), new PointF(left, bottom), new PointF(left, to.Y), to], r);
    }

    private static PointF[] RoundedPolyline(PointF[] corners, float radius)
    {
        var result = new List<PointF> { corners[0] };
        for (int k = 1; k < corners.Length - 1; k++)
        {
            var p = corners[k];
            var d1 = Direction(corners[k - 1], p);
            var d2 = Direction(p, corners[k + 1]);
            float r1 = Math.Min(radius, Distance(corners[k - 1], p) / 2);
            float r2 = Math.Min(radius, Distance(p, corners[k + 1]) / 2);
            var a = new PointF(p.X - d1.X * r1, p.Y - d1.Y * r1);
            var b = new PointF(p.X + d2.X * r2, p.Y + d2.Y * r2);
            for (int s = 0; s <= 6; s++)
            {
                float t = s / 6f, u = 1 - t;
                result.Add(new PointF(u * u * a.X + 2 * u * t * p.X + t * t * b.X, u * u * a.Y + 2 * u * t * p.Y + t * t * b.Y));
            }
        }
        result.Add(corners[^1]);
        return [.. result];
    }

    private static PointF Direction(PointF a, PointF b)
    {
        float d = Distance(a, b);
        return d < 0.001f ? PointF.Empty : new PointF((b.X - a.X) / d, (b.Y - a.Y) / d);
    }

    public static float Distance(PointF a, PointF b) => MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    /// <summary>Khoảng cách từ điểm tới dây (đường gấp khúc).</summary>
    public static float DistanceTo(GraphEdge edge, PointF p)
    {
        float best = float.MaxValue;
        var pts = edge.Points;
        for (int k = 1; k < pts.Length; k++) best = Math.Min(best, SegmentDistance(p, pts[k - 1], pts[k]));
        return best;
    }

    private static float SegmentDistance(PointF p, PointF a, PointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
        float t = len < 1e-6f ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
        return Distance(p, new PointF(a.X + t * dx, a.Y + t * dy));
    }

    private void BuildJumps()
    {
        for (int i = 0; i < _steps.Count; i++)
        {
            if (!IsStepNode(i)) continue;
            var s = _steps[i];
            if (s.Type == StepType.Goto && _fs.Labels.TryGetValue(s.Target.Trim(), out int to) && IsStepNode(to))
                Jumps.Add(new GraphJump(i, to, false));
            else if (s.OnError == ErrorAction.GotoLabel && !s.IsControl && _fs.Labels.TryGetValue(s.ErrorLabel.Trim(), out int target) && IsStepNode(target))
                Jumps.Add(new GraphJump(i, target, true));
        }
    }

    /// <summary>Điểm cao nhất của dây nhảy giữa hai nút (vòng lên phía trên).</summary>
    public float JumpTop(GraphJump j)
    {
        var a = Bounds[j.From]!.Value;
        var b = Bounds[j.To]!.Value;
        return Math.Min(a.Top, b.Top) - _m.Node * 0.45f - Math.Abs(a.X - b.X) * 0.08f;
    }

    private RectangleF ComputeContentBounds()
    {
        var box = StartBounds;
        box = RectangleF.Union(box, StubBounds);
        float extra = (_m.LabelWidth - _m.Node) / 2;
        foreach (var r in Bounds)
            if (r is { } b) box = RectangleF.Union(box, new RectangleF(b.X - extra, b.Y, b.Width + 2 * extra, b.Height + _m.Label));
        foreach (var e in Edges)
            foreach (var p in e.Points) box = RectangleF.Union(box, new RectangleF(p.X, p.Y, 1, 1));
        foreach (var j in Jumps)
            box = RectangleF.Union(box, new RectangleF(box.X, JumpTop(j), 1, 1));
        return box;
    }
}
