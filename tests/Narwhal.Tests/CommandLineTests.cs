using Narwhal.Cli;
using Xunit;

namespace Narwhal.Tests;

public class CommandLineTests
{
    [Fact]
    public void ParsesCommandAndPositionals()
    {
        var options = CommandLine.Parse(["build", "api", "db"]);

        Assert.Equal("build", options.Command);
        Assert.Equal(["api", "db"], options.Positionals);
    }

    [Fact]
    public void ParsesGlobalOptionsBeforeAndAfterTheCommand()
    {
        var options = CommandLine.Parse(["--json", "-v", "plan"]);

        Assert.True(options.Json);
        Assert.True(options.Verbose);
        Assert.Equal("plan", options.Command);
    }

    [Fact]
    public void SupportsEqualsSyntax()
    {
        var options = CommandLine.Parse(["logs", "--tail=50", "api"]);

        Assert.Equal("50", options.Value("tail"));
        Assert.Equal(["api"], options.Positionals);
        Assert.Equal(50, options.IntValue("tail"));
    }

    [Fact]
    public void FollowsContainersWithShortF()
    {
        var options = CommandLine.Parse(["logs", "-f", "api"]);

        Assert.True(options.Flag("follow"));
    }

    [Fact]
    public void UsesLongFileFlagWithoutStealingShortF()
    {
        var options = CommandLine.Parse(["up", "--file", "custom.toml", "-C", "sub"]);

        Assert.Equal("custom.toml", options.File);
        Assert.Equal("sub", options.Directory);
    }

    [Fact]
    public void TreatsEverythingAfterDoubleDashAsPositionals()
    {
        var options = CommandLine.Parse(["logs", "--", "-weird-name"]);

        Assert.Equal(["-weird-name"], options.Positionals);
    }

    [Fact]
    public void RejectsUnknownCommands()
    {
        var error = Assert.Throws<UsageException>(() => CommandLine.Parse(["frobnicate"]));
        Assert.Contains("unknown command 'frobnicate'", error.Message);
    }

    [Fact]
    public void RejectsUnknownOptionsPerCommand()
    {
        var error = Assert.Throws<UsageException>(() => CommandLine.Parse(["ps", "--tail", "10"]));
        Assert.Contains("unknown option '--tail'", error.Message);
    }

    [Fact]
    public void RejectsMissingOptionValues()
    {
        var error = Assert.Throws<UsageException>(() => CommandLine.Parse(["logs", "--tail"]));
        Assert.Contains("requires a value", error.Message);
    }

    [Fact]
    public void ValidatesIntegerOptions()
    {
        var options = CommandLine.Parse(["logs", "--tail", "abc"]);
        Assert.Throws<UsageException>(() => options.IntValue("tail"));
    }

    [Fact]
    public void MapsDeployToUp()
    {
        var options = CommandLine.Parse(["deploy"]);
        Assert.Equal("deploy", options.Command);
        Assert.NotNull(CommandLine.Find(options.Command));
    }

    [Fact]
    public void HelpDoesNotRequireACommand()
    {
        var options = CommandLine.Parse(["-h"]);
        Assert.True(options.Help);
    }

    [Fact]
    public void CollectsRepeatedSystemValues()
    {
        var options = CommandLine.Parse(["build", "--system", "x86_64-linux", "--system=i686-linux"]);

        Assert.Equal(["x86_64-linux", "i686-linux"], options.ListValue("system"));
        Assert.Equal("i686-linux", options.Value("system"));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("verify")]
    [InlineData("push")]
    public void SystemIsAValueOptionOnImageCommands(string command)
    {
        var options = CommandLine.Parse([command, "--system", "aarch64-linux"]);
        Assert.Equal(["aarch64-linux"], options.ListValue("system"));
    }

    [Fact]
    public void RejectsSystemOnCommandsWithoutIt()
    {
        var error = Assert.Throws<UsageException>(() => CommandLine.Parse(["plan", "--system", "x86_64-linux"]));
        Assert.Contains("unknown option '--system'", error.Message);
    }
}
