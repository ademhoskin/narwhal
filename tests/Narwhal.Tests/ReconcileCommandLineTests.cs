using Narwhal.Cli;
using Xunit;

namespace Narwhal.Tests;

public class ReconcileCommandLineTests
{
    [Fact]
    public void ParsesConfigAndJson()
    {
        var options = CommandLine.Parse(["reconcile", "--config", "/etc/narwhal/agent.toml", "--json"]);

        Assert.Equal("reconcile", options.Command);
        Assert.Equal("/etc/narwhal/agent.toml", options.Value("config"));
        Assert.True(options.Json);
    }

    [Fact]
    public void SupportsEqualsSyntax()
    {
        var options = CommandLine.Parse(["reconcile", "--config=/tmp/agent.toml"]);

        Assert.Equal("/tmp/agent.toml", options.Value("config"));
    }

    [Fact]
    public void ReconcileIsInTheCommandTable()
    {
        var spec = CommandLine.Find("reconcile");

        Assert.NotNull(spec);
        Assert.Contains("reconcile", CommandLine.Usage(), StringComparison.Ordinal);
        Assert.Contains("--config", CommandLine.Usage(spec!), StringComparison.Ordinal);
    }

    [Fact]
    public void UpStillRejectsCloudOptions()
    {
        var error = Assert.Throws<UsageException>(() => CommandLine.Parse(["up", "--config", "x"]));
        Assert.Contains("unknown option '--config'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployStillMapsToUp()
    {
        var options = CommandLine.Parse(["deploy"]);

        Assert.Equal("deploy", options.Command);
        Assert.Equal("up", CommandLine.Find("deploy")!.Name);
    }
}
