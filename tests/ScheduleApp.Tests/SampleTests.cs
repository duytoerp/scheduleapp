using System.Reflection;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Data;

namespace ScheduleApp.Tests;

public class SampleTests
{
    public static IEnumerable<object[]> SampleFiles() =>
        typeof(Job).Assembly.GetManifestResourceNames().Where(n => n.EndsWith(".json")).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void EmbeddedSamplesAreValid(string resource)
    {
        using var stream = typeof(Job).Assembly.GetManifestResourceStream(resource)!;
        var jobs = JobStore.ImportJson(new StreamReader(stream).ReadToEnd());
        Assert.NotEmpty(jobs);
        foreach (var job in jobs)
        {
            var fs = FlowStructure.Build(job.Steps);
            Assert.True(fs.IsValid, $"{job.Name}: {string.Join("; ", fs.Errors)}");
            // "Chạy công việc khác" phải trỏ tới công việc trong cùng file (Id được gán lại khi nhập).
            foreach (var s in job.Steps.Where(s => s.Type == StepType.CallJob))
                Assert.Contains(jobs, j => j.Id == s.JobRef);
            // Mẫu có lịch / trình kích hoạt không được tự bật khi người dùng thêm vào.
            if (job.Schedule.Type != ScheduleType.Manual || job.Triggers.Count > 0) Assert.False(job.Enabled, job.Name);
            foreach (var s in job.Steps.Where(s => s.Type == StepType.WriteData))
                Assert.NotEmpty(TabularWriter.ParseAssignments(s.Text, x => x));
        }
    }

    [Fact]
    public void ThreeSampleFilesAreEmbedded() => Assert.Equal(3, SampleFiles().Count());
}
