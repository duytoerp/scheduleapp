using System.Text.Json;
using System.Text.RegularExpressions;
using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>
/// Gom những gì người dùng cần điền khi dùng mẫu (biến như tenant / d365Url, bí mật, kết nối API, đường dẫn file / thư mục)
/// từ một nhóm công việc — giá trị giống nhau ở nhiều công việc gộp thành một ô — và áp các giá trị mới vào đúng chỗ.
/// </summary>
public static partial class TemplateSetup
{
    public enum ItemKind { Variable, Secret, Path, Connection }

    public sealed class Item
    {
        public ItemKind Kind { get; init; }
        /// <summary>Tên biến / bí mật / kết nối, hoặc đường dẫn gốc.</summary>
        public string Key { get; init; } = "";
        /// <summary>Giá trị hiện có (biến: của công việc đầu tiên; đường dẫn: chính đường dẫn).</summary>
        public string Value { get; init; } = "";
        /// <summary>Giá trị gợi ý điền sẵn (đã nhập ở lần thiết lập trước) — null = dùng <see cref="Value"/>.</summary>
        public string? Suggested { get; init; }
        public string Description { get; init; } = "";
        public List<Job> Jobs { get; } = [];
        /// <summary>Biến có giá trị khác nhau giữa các công việc (chỉ ghi đè khi người dùng sửa).</summary>
        public bool Mixed { get; set; }
        /// <summary>Bí mật đã có / kết nối đã khai báo.</summary>
        public bool Exists { get; init; }
        /// <summary>Đường dẫn là thư mục (chọn bằng hộp chọn thư mục).</summary>
        public bool IsFolder { get; init; }

        /// <summary>Giá trị còn là chữ mẫu (ten-cong-ty, tenorg, Id toàn số 0…) — cần sửa trước khi chạy.</summary>
        public bool NeedsInput => Kind switch
        {
            ItemKind.Variable => IsPlaceholder(Suggested ?? Value),
            ItemKind.Secret or ItemKind.Connection => !Exists,
            _ => false
        };

        public string JobNames => string.Join(", ", Jobs.Select(j => ShortName(j.Name)));
    }

    [GeneratedRegex(@"\{\{\s*secret:([^}]+?)\s*\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"ten-cong-ty|tenorg|your-?org|<[^>]+>|00000000-0000-0000-0000-000000000000|example\.(com|vn)", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderPattern();

    public static bool IsPlaceholder(string value) => PlaceholderPattern().IsMatch(value);

    /// <summary>Các mục cần thiết lập của nhóm công việc (thứ tự: biến, bí mật, kết nối, đường dẫn).</summary>
    /// <param name="remember">Gợi ý giá trị đã nhập trước đó cho biến cùng tên (dùng khi thêm mẫu mới).</param>
    public static List<Item> Collect(IReadOnlyCollection<Job> jobs, bool remember)
    {
        var items = new List<Item>();
        var settings = SettingsStore.Current;

        // ── Biến (bỏ qua biến làm việc được flow tự gán, vd bộ đếm, danh sách) ──
        foreach (var job in jobs)
        {
            var assigned = job.Steps.Where(s => s.Type == StepType.SetVariable).Select(s => s.Variable.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var v in job.Variables.Where(v => v.Name.Trim().Length > 0 && !assigned.Contains(v.Name.Trim())))
            {
                var item = items.FirstOrDefault(i => i.Kind == ItemKind.Variable && i.Key.Equals(v.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    string? suggested = null;
                    if (remember && IsPlaceholder(v.Value))
                        suggested = settings.TemplateValues.FirstOrDefault(kv => kv.Key.Equals(v.Name.Trim(), StringComparison.OrdinalIgnoreCase)).Value;
                    item = new Item { Kind = ItemKind.Variable, Key = v.Name.Trim(), Value = v.Value, Suggested = suggested, Description = v.Description ?? "" };
                    items.Add(item);
                }
                else if (item.Value != v.Value) item.Mixed = true;
                if (!item.Jobs.Contains(job)) item.Jobs.Add(job);
            }
        }

        // ── Bí mật ({{secret:Tên}} ở bất kỳ ô nào) ──
        foreach (var job in jobs)
        {
            var json = JsonSerializer.Serialize(job.Steps, JsonDefaults.Options);
            foreach (var name in SecretPattern().Matches(json).Select(m => m.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var item = items.FirstOrDefault(i => i.Kind == ItemKind.Secret && i.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    item = new Item { Kind = ItemKind.Secret, Key = name, Exists = SecretStore.Contains(name) };
                    items.Add(item);
                }
                if (!item.Jobs.Contains(job)) item.Jobs.Add(job);
            }
        }

        // ── Kết nối API ──
        foreach (var job in jobs)
            foreach (var name in job.Steps.Select(s => s.Connection.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var item = items.FirstOrDefault(i => i.Kind == ItemKind.Connection && i.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    item = new Item
                    {
                        Kind = ItemKind.Connection, Key = name,
                        Exists = settings.ApiConnections.Any(c => c.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    };
                    items.Add(item);
                }
                if (!item.Jobs.Contains(job)) item.Jobs.Add(job);
            }

        // ── Đường dẫn file / thư mục ghi thẳng trong bước / trình kích hoạt ──
        foreach (var job in jobs)
            foreach (var (path, folder) in PathsOf(job))
            {
                var item = items.FirstOrDefault(i => i.Kind == ItemKind.Path && i.Key.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    item = new Item { Kind = ItemKind.Path, Key = path, Value = path, IsFolder = folder };
                    items.Add(item);
                }
                if (!item.Jobs.Contains(job)) item.Jobs.Add(job);
            }

        return [.. items.OrderBy(i => i.Kind)];
    }

    /// <summary>Đường dẫn trong các ô chứa file / thư mục (không gồm ô chỉ là một {{biến}}).</summary>
    private static IEnumerable<(string Path, bool Folder)> PathsOf(Job job)
    {
        foreach (var s in job.Steps)
            foreach (var (value, folder) in PathFields(s).Select(f => (f.Get(), f.Folder)))
                if (IsPathLike(value)) yield return (value.Trim(), folder ?? !Path.HasExtension(StripVars(value)));
        foreach (var t in job.Triggers.Where(t => t.Type == TriggerType.FileCreated))
            if (IsPathLike(t.Value)) yield return (t.Value.Trim(), true);
    }

    private sealed record PathField(Func<string> Get, Action<string> Set, bool? Folder);

    private static IEnumerable<PathField> PathFields(ActionStep s)
    {
        PathField Target(bool? folder) => new(() => s.Target, v => s.Target = v, folder);
        switch (s.Type)
        {
            case StepType.LaunchApp:
                yield return Target(null);
                break;
            case StepType.WriteData:
                yield return Target(false);
                break;
            case StepType.SetVariable when s.VarSource == VarSource.File:
                yield return Target(false);
                break;
            case StepType.Loop when s.LoopKind is LoopKind.Rows or LoopKind.Lines:
                yield return Target(false);
                break;
            case StepType.Loop when s.LoopKind == LoopKind.Files:
                yield return Target(true);
                break;
        }
        if (s.HasCondition && s.Condition == ConditionKind.FileExists) yield return Target(null);
    }

    private static bool IsPathLike(string value)
    {
        var v = value.Trim();
        if (v.Length < 3 || v.Contains("://") || Regex.IsMatch(v, @"^\{\{[^}]+\}\}$")) return false;
        return v.Contains('\\') || Regex.IsMatch(v, @"^[A-Za-z]:") || v.StartsWith('%');
    }

    private static string StripVars(string value) => Regex.Replace(value, @"\{\{[^}]*\}\}", "x");

    /// <summary>Áp các giá trị người dùng đã sửa (mục → giá trị mới). Bí mật trống = giữ nguyên.</summary>
    /// <returns>Số chỗ đã thay đổi.</returns>
    public static int Apply(IReadOnlyDictionary<Item, string> values)
    {
        int changes = 0;
        var settings = SettingsStore.Current;
        foreach (var (item, raw) in values)
        {
            var value = item.Kind == ItemKind.Secret ? raw : raw.Trim();
            switch (item.Kind)
            {
                case ItemKind.Variable:
                    // Không sửa → giữ nguyên (kể cả khi mỗi công việc đang có giá trị riêng).
                    if (value == item.Value) break;
                    foreach (var job in item.Jobs)
                        foreach (var v in job.Variables.Where(v => v.Name.Trim().Equals(item.Key, StringComparison.OrdinalIgnoreCase) && v.Value != value))
                        {
                            v.Value = value;
                            changes++;
                        }
                    if (!IsPlaceholder(value) && value.Length > 0) settings.TemplateValues[item.Key] = value;
                    break;

                case ItemKind.Secret:
                    if (value.Length == 0) break;
                    SecretStore.Set(item.Key, value);
                    changes++;
                    break;

                case ItemKind.Path:
                    if (value.Length == 0 || value.Equals(item.Key, StringComparison.Ordinal)) break;
                    foreach (var job in item.Jobs)
                    {
                        foreach (var f in job.Steps.SelectMany(PathFields))
                            if (f.Get().Trim().Equals(item.Key, StringComparison.OrdinalIgnoreCase))
                            {
                                f.Set(value);
                                changes++;
                            }
                        foreach (var t in job.Triggers.Where(t => t.Type == TriggerType.FileCreated && t.Value.Trim().Equals(item.Key, StringComparison.OrdinalIgnoreCase)))
                        {
                            t.Value = value;
                            changes++;
                        }
                    }
                    break;
            }
        }
        if (changes > 0) SettingsStore.Save();
        return changes;
    }

    /// <summary>Công việc kèm các công việc nó gọi tới (bước "Chạy công việc khác", công việc xử lý lỗi), lặp đệ quy.</summary>
    public static List<Job> WithDependencies(Job job, IReadOnlyCollection<Job> all)
    {
        var result = new List<Job> { job };
        for (int i = 0; i < result.Count; i++)
        {
            var refs = result[i].Steps.Where(s => s.JobRef != null).Select(s => s.JobRef!.Value).ToList();
            if (result[i].OnFailureJobId is Guid f) refs.Add(f);
            foreach (var dep in all.Where(j => refs.Contains(j.Id) && !result.Contains(j))) result.Add(dep);
        }
        return result;
    }

    private static string ShortName(string name)
    {
        int dot = name.IndexOf(" · ", StringComparison.Ordinal);
        return dot is > 0 and <= 4 ? name[..dot] : name.Length > 40 ? name[..40] + "…" : name;
    }
}
