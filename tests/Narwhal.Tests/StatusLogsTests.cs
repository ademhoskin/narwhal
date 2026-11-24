using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class StatusLogsTests
{
    [Fact]
    public void RedactsResolvedSecretValuesAndPatterns()
    {
        var lines = new[]
        {
            new StatusLogLine
            {
                Service = "api",
                At = "2026-11-24T18:31:05Z",
                Line = "token=hunter2 password=swordfish",
            },
        };

        var bounded = StatusLogs.BoundAndRedact(lines, ["hunter2"], ["swordfish"]);

        var line = Assert.Single(bounded);
        Assert.DoesNotContain("hunter2", line.Line, StringComparison.Ordinal);
        Assert.DoesNotContain("swordfish", line.Line, StringComparison.Ordinal);
        Assert.Equal("token=[redacted] password=[redacted]", line.Line);
    }

    [Fact]
    public void KeepsTheNewest200Lines()
    {
        var lines = Enumerable.Range(0, 250)
            .Select(index => new StatusLogLine
            {
                Service = "api",
                At = "2026-11-24T18:31:05Z",
                Line = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

        var bounded = StatusLogs.BoundAndRedact(lines);

        Assert.Equal(StatusLogs.MaxLines, bounded.Count);
        Assert.Equal("50", bounded[0].Line);
        Assert.Equal("249", bounded[^1].Line);
    }

    [Fact]
    public void KeepsTheNewest32KiB()
    {
        var line = new string('x', 1000);
        var lines = Enumerable.Range(0, 40)
            .Select(_ => new StatusLogLine { Service = "api", At = "2026-11-24T18:31:05Z", Line = line });

        var bounded = StatusLogs.BoundAndRedact(lines);

        Assert.Equal(32, bounded.Count);
        Assert.True(
            bounded.Sum(statusLine => System.Text.Encoding.UTF8.GetByteCount(statusLine.Line)) <=
            StatusLogs.MaxBytes);
    }
}
