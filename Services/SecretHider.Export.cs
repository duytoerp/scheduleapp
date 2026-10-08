using System.Text.RegularExpressions;
using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>
/// Bí mật ghi thẳng trong công việc (không qua 🔑 Bí mật) khi xuất file / thư mục kịch bản: mật khẩu / khóa TOTP của bước đăng nhập
/// D365 (chữ thường hoặc đã mã hóa — bản mã hóa vô dụng ở máy khác nhưng vẫn bẻ được ngoại tuyến), header kiểu Authorization / x-api-key
/// của bước HTTP, biến / bước gán biến tên kiểu mật khẩu / token, chữ nhập vào ô mật khẩu. Chỗ giữ chỗ {{secret:…}} / {{biến}} không tính.
/// </summary>
internal sealed partial class SecretHider
{
    /// <summary>Mô tả các bí mật ghi thẳng trong công việc (và biến môi trường kiểm thử) sẽ nằm trong file xuất.</summary>
    public static List<string> FindLiteralSecrets(IEnumerable<Job> jobs, IEnumerable<TestEnvironment>? environments = null)
    {
        var found = new List<string>();
        foreach (var job in jobs)
        {
            foreach (var v in job.Variables)
                if (StripVariable(new VariableDef { Name = v.Name, Value = v.Value })) found.Add($"\"{job.Name}\": biến {v.Name}");
            for (int i = 0; i < job.Steps.Count; i++)
                foreach (var what in StripStep(job.Steps[i].ShallowCopy()))
                    found.Add($"\"{job.Name}\" bước {i + 1}: {what}");
        }
        foreach (var env in environments ?? [])
            foreach (var v in env.Variables)
                if (StripVariable(new VariableDef { Name = v.Name, Value = v.Value })) found.Add($"môi trường \"{env.Name}\": biến {v.Name}");
        return found;
    }

    /// <summary>Bản sao của công việc đã bỏ bí mật ghi thẳng (công việc gốc không đổi).</summary>
    public static Job WithoutLiteralSecrets(Job job)
    {
        var copy = job.Clone();
        foreach (var v in copy.Variables) StripVariable(v);
        foreach (var s in copy.Steps) StripStep(s);
        return copy;
    }

    /// <summary>Bản sao của môi trường kiểm thử đã bỏ giá trị biến tên kiểu mật khẩu / token.</summary>
    public static TestEnvironment WithoutLiteralSecrets(TestEnvironment env)
    {
        var copy = new TestEnvironment { Name = env.Name, Variables = [.. env.Variables.Select(v => new VariableDef { Name = v.Name, Value = v.Value, Description = v.Description })] };
        foreach (var v in copy.Variables) StripVariable(v);
        return copy;
    }

    /// <summary>Biến tên kiểu mật khẩu / token có giá trị ghi thẳng → xóa giá trị. True nếu đã xóa.</summary>
    private static bool StripVariable(VariableDef v)
    {
        if (!IsSecretName(v.Name) || !IsLiteral(v.Value)) return false;
        v.Value = "";
        return true;
    }

    /// <summary>
    /// Bỏ bí mật ghi thẳng trong bước (sửa chính <paramref name="s"/> — truyền bản sao): trường mật khẩu → rỗng, header bí mật →
    /// {{secret:Tên header}} (tạo bí mật cùng tên ở máy nhận). Trả về mô tả các trường đã bỏ.
    /// </summary>
    private static List<string> StripStep(ActionStep s)
    {
        var removed = new List<string>();
        foreach (var field in SecretFields(s))
        {
            var p = typeof(ActionStep).GetProperty(field)!;
            if (p.GetValue(s) is not string value || !IsLiteral(value)) continue;
            p.SetValue(s, "");
            removed.Add(FieldName(s, field));
        }
        if (s.Type == StepType.HttpRequest && s.Headers.Length > 0)
        {
            var headers = HeaderLine().Replace(s.Headers, m =>
            {
                if (!IsLiteral(m.Groups[2].Value)) return m.Value;
                var name = m.Groups[1].Value.Trim().TrimEnd(':').Trim();
                removed.Add("header " + name);
                return m.Groups[1].Value + "{{secret:" + Regex.Replace(name, @"[^\w-]", "") + "}}";
            });
            s.Headers = headers;
        }
        return removed;
    }

    private static string FieldName(ActionStep s, string field) => (s.Type, field) switch
    {
        (StepType.Dynamics, nameof(ActionStep.Arguments)) => "mật khẩu đăng nhập D365",
        (StepType.Dynamics, nameof(ActionStep.RowRef)) => "khóa TOTP đăng nhập D365",
        (StepType.SetVariable, nameof(ActionStep.Text)) => $"giá trị gán cho {{{{{s.Variable}}}}}",
        _ => "chữ nhập vào ô mật khẩu"
    };

    /// <summary>Giá trị ghi thẳng: không rỗng và không chỉ gồm {{…}} (có thể kèm chữ Bearer / Basic).</summary>
    private static bool IsLiteral(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var rest = AnyTemplate().Replace(value, "").Trim();
        return !(rest.Length < value.Trim().Length && (rest.Length == 0 || SchemeOnly().IsMatch(rest)));
    }
}
