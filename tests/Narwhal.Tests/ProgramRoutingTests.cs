using Xunit;

namespace Narwhal.Tests;

[Collection(MultiPlatformIntegrationTests.CliProcessSerial.Name)]
public class ProgramRoutingTests
{
    [Fact]
    public async Task NoArgumentsPrintsUsageAndExitsZero()
    {
        var result = await Run();

        Assert.Equal(0, result.Exit);
        Assert.Contains("usage: narwhal <command>", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommandExitsOne()
    {
        var result = await Run("frobnicate");

        Assert.Equal(1, result.Exit);
        Assert.Contains("unknown command 'frobnicate'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DaemonReachesTheAgentCommandsForAMissingConfig()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "agent.toml");

        var result = await Run("daemon", "--config", missing);

        Assert.Equal(1, result.Exit);
        Assert.Contains("agent config not found", result.Error, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> ManifestOptionCases()
    {
        var commands = new[] { "daemon", "guard", "install-units", "install-guard" };
        var options = new (string Option, string Value, string Reported)[]
        {
            ("-C", "/tmp", "--dir"),
            ("--dir", "/tmp", "--dir"),
            ("--file", "narwhal.toml", "--file"),
            ("--lock", "narwhal.lock.json", "--lock"),
        };

        foreach (var command in commands)
            foreach (var option in options)
                yield return [command, option.Option, option.Value, option.Reported];
    }

    [Theory]
    [MemberData(nameof(ManifestOptionCases))]
    public async Task AgentCommandsRejectManifestOptions(
        string command,
        string option,
        string value,
        string reported)
    {
        var result = await Run(option, value, command);

        Assert.Equal(1, result.Exit);
        Assert.Contains($"option '{reported}' is not valid for '{command}'", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("daemon")]
    [InlineData("guard")]
    [InlineData("install-units")]
    [InlineData("install-guard")]
    public async Task AgentCommandsRejectQuiet(string command)
    {
        var result = await Run("--quiet", command);

        Assert.Equal(1, result.Exit);
        Assert.Contains($"option '--quiet' is not valid for '{command}'", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("reconcile", true)]
    [InlineData("daemon", true)]
    [InlineData("guard", true)]
    [InlineData("install-units", true)]
    [InlineData("install-guard", true)]
    [InlineData("plan", false)]
    [InlineData("status", false)]
    public void RegistersSigtermOnlyForLongRunningOrAgentCommands(string command, bool expected)
    {
        Assert.Equal(expected, App.RegistersSigterm(command));
    }

    private static async Task<(int Exit, string Output, string Error)> Run(params string[] args)
    {
        var previousEncoding = Console.OutputEncoding;
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var output = new StringWriter();
        var errors = new StringWriter();
        Console.SetOut(output);
        Console.SetError(errors);
        try
        {
            var exit = await App.Main(args);
            return (exit, output.ToString(), errors.ToString());
        }
        finally
        {
            Console.OutputEncoding = previousEncoding;
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }
}
