using ScheduleApp.Services.Engine;

namespace ScheduleApp.UI;

/// <summary>
/// Chọn nhanh công thức sinh dữ liệu test ngẫu nhiên (=hoten(), =email()…) cho ô giá trị biến, và tô / chú thích các ô đang dùng công thức.
/// </summary>
internal static class RandomValueMenu
{
    public static readonly Color FormulaColor = Color.FromArgb(110, 60, 190);

    /// <summary>Menu các hàm, mỗi mục kèm ví dụ giá trị sinh ra; chọn → gọi <paramref name="apply"/>(hàm, công thức).</summary>
    public static void Show(Control anchor, Action<TestData.Function, string> apply)
    {
        var menu = new ContextMenuStrip { ShowItemToolTips = true };
        menu.Items.Add(new ToolStripLabel("Giá trị ngẫu nhiên — sinh mới ở mỗi lần chạy") { ForeColor = SystemColors.GrayText });
        foreach (var f in TestData.Functions)
        {
            string sample;
            try { sample = TestData.Evaluate(f.Example); }
            catch (FormatException) { sample = ""; }
            var item = new ToolStripMenuItem($"{f.Example}     {f.Description}") { ToolTipText = $"{f.Syntax}\nVí dụ: {sample}" };
            var fn = f;
            item.Click += (_, _) => apply(fn, fn.Example);
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => anchor.BeginInvoke(new MethodInvoker(menu.Dispose));
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    /// <summary>Chú thích cho ô giá trị: ví dụ giá trị sinh ra, hoặc lỗi cú pháp. null = không phải công thức.</summary>
    public static string? Describe(string? value) => TestData.Preview(value) switch
    {
        { Ok: true } p => $"⚄ Mỗi lần chạy sinh giá trị mới, vd: {p.Text}",
        { Ok: false } p => "✖ " + p.Text,
        _ => null
    };

    /// <summary>
    /// Nút "🎲 Giá trị ngẫu nhiên" cho lưới biến (cột 0 = tên, cột 1 = giá trị): ghi công thức vào dòng đang chọn,
    /// hoặc thêm dòng mới với tên gợi ý; ô dùng công thức được tô màu và có chú thích ví dụ.
    /// </summary>
    public static Button GridButton(DataGridView grid)
    {
        var button = new Button { Text = "⚄ Giá trị ngẫu nhiên ▾", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        button.Click += (_, _) => Show(button, (fn, formula) =>
        {
            grid.EndEdit();
            // Đang chọn một biến → đặt công thức cho biến đó; chưa chọn (dòng trống cuối) → thêm biến mới với tên gợi ý.
            DataGridViewRow row;
            if (grid.CurrentRow is { IsNewRow: false } current)
            {
                row = current;
                if (string.IsNullOrWhiteSpace(row.Cells[0].Value as string)) row.Cells[0].Value = UniqueName(grid, SuggestedName(fn.Name));
                row.Cells[1].Value = formula;
            }
            else
            {
                row = grid.Rows[grid.Rows.Add(UniqueName(grid, SuggestedName(fn.Name)), formula)];
            }
            grid.CurrentCell = row.Cells[1];
        });
        Attach(grid);
        return button;
    }

    /// <summary>Tô màu + chú thích ô giá trị dùng công thức.</summary>
    public static void Attach(DataGridView grid)
    {
        grid.ShowCellToolTips = true;
        grid.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex != 1 || e.RowIndex < 0) return;
            var text = Describe(e.Value as string);
            grid.Rows[e.RowIndex].Cells[1].ToolTipText = text ?? "";
            if (text == null) return;
            e.CellStyle!.ForeColor = text.StartsWith('✖') ? Color.FromArgb(190, 40, 20) : FormulaColor;
        };
    }

    private static string SuggestedName(string function) => function switch
    {
        "hoten" => "hoTen",
        "sdt" => "soDienThoai",
        "diachi" => "diaChi",
        "thanhpho" => "thanhPho",
        "congty" => "congTy",
        "random" => "so",
        "chuso" => "ma",
        "chon" => "luaChon",
        "guid" => "id",
        _ => function
    };

    private static string UniqueName(DataGridView grid, string name)
    {
        var used = grid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => (r.Cells[0].Value as string ?? "").Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name)) return name;
        for (int i = 2; ; i++)
            if (!used.Contains(name + i)) return name + i;
    }
}
