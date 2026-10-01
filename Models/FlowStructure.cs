namespace ScheduleApp.Models;

/// <summary>
/// Phân tích cấu trúc khối của flow phẳng: ghép Nếu/Không thì/Hết Nếu, Lặp/Hết lặp, tra nhãn,
/// tính độ thụt lề để hiển thị và phát hiện lỗi cấu trúc.
/// </summary>
public sealed class FlowStructure
{
    /// <summary>Vị trí khối tương ứng: If→EndIf, Else→EndIf, EndIf→If, Loop→EndLoop, EndLoop→Loop; -1 nếu không có.</summary>
    public int[] Match { get; }

    /// <summary>If → vị trí Else của nó (-1 nếu không có).</summary>
    public int[] ElseOf { get; }

    /// <summary>Độ thụt lề khi hiển thị.</summary>
    public int[] Depth { get; }

    /// <summary>Bước có lỗi cấu trúc (để tô đỏ).</summary>
    public bool[] Invalid { get; }

    public Dictionary<string, int> Labels { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Errors { get; } = [];

    public bool IsValid => Errors.Count == 0;

    private FlowStructure(int n)
    {
        Match = Enumerable.Repeat(-1, n).ToArray();
        ElseOf = Enumerable.Repeat(-1, n).ToArray();
        Depth = new int[n];
        Invalid = new bool[n];
    }

    public static FlowStructure Build(IReadOnlyList<ActionStep> steps)
    {
        var fs = new FlowStructure(steps.Count);
        var stack = new List<int>();

        void Fail(int i, string message)
        {
            fs.Invalid[i] = true;
            fs.Errors.Add($"Bước {i + 1}: {message}");
        }

        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            int top = stack.Count > 0 ? stack[^1] : -1;
            switch (s.Type)
            {
                case StepType.If:
                case StepType.Loop:
                    fs.Depth[i] = stack.Count;
                    stack.Add(i);
                    break;

                case StepType.Else:
                    if (top >= 0 && steps[top].Type == StepType.If && fs.ElseOf[top] < 0)
                    {
                        fs.ElseOf[top] = i;
                        fs.Depth[i] = stack.Count - 1;
                    }
                    else
                    {
                        fs.Depth[i] = stack.Count;
                        Fail(i, "\"Không thì\" không nằm trong khối \"Nếu\" (hoặc khối đã có \"Không thì\").");
                    }
                    break;

                case StepType.EndIf:
                    if (top >= 0 && steps[top].Type == StepType.If)
                    {
                        stack.RemoveAt(stack.Count - 1);
                        fs.Match[top] = i;
                        fs.Match[i] = top;
                        if (fs.ElseOf[top] >= 0) fs.Match[fs.ElseOf[top]] = i;
                        fs.Depth[i] = stack.Count;
                    }
                    else
                    {
                        fs.Depth[i] = stack.Count;
                        Fail(i, "\"Hết Nếu\" không có \"Nếu\" tương ứng.");
                    }
                    break;

                case StepType.EndLoop:
                    if (top >= 0 && steps[top].Type == StepType.Loop)
                    {
                        stack.RemoveAt(stack.Count - 1);
                        fs.Match[top] = i;
                        fs.Match[i] = top;
                        fs.Depth[i] = stack.Count;
                    }
                    else
                    {
                        fs.Depth[i] = stack.Count;
                        Fail(i, "\"Hết lặp\" không có \"Lặp\" tương ứng.");
                    }
                    break;

                case StepType.BreakLoop or StepType.ContinueLoop:
                    fs.Depth[i] = stack.Count;
                    if (!stack.Any(b => steps[b].Type == StepType.Loop))
                        Fail(i, $"\"{ActionStep.TypeNames[s.Type]}\" nằm ngoài vòng lặp.");
                    break;

                case StepType.Label:
                    fs.Depth[i] = stack.Count;
                    var name = s.Target.Trim();
                    if (name.Length == 0) Fail(i, "Nhãn chưa có tên.");
                    else if (!fs.Labels.TryAdd(name, i)) Fail(i, $"Trùng tên nhãn \"{name}\".");
                    break;

                default:
                    fs.Depth[i] = stack.Count;
                    break;
            }
        }

        foreach (var open in stack)
            Fail(open, steps[open].Type == StepType.If ? "Khối \"Nếu\" chưa có \"Hết Nếu\"." : "Khối \"Lặp\" chưa có \"Hết lặp\".");

        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            if (s.Type == StepType.Goto && !fs.Labels.ContainsKey(s.Target.Trim()))
                Fail(i, $"Không có nhãn \"{s.Target}\".");
            if (s.OnError == ErrorAction.GotoLabel && !s.IsControl && !fs.Labels.ContainsKey(s.ErrorLabel.Trim()))
                Fail(i, $"Khi lỗi nhảy tới nhãn \"{s.ErrorLabel}\" nhưng không có nhãn này.");
        }
        return fs;
    }

    /// <summary>Vị trí <paramref name="index"/> có nằm bên trong khối bắt đầu tại <paramref name="blockStart"/> không.</summary>
    public bool Contains(int blockStart, int index) =>
        blockStart >= 0 && Match[blockStart] >= 0 && index > blockStart && index < Match[blockStart];
}
