using Narwhal.Cli;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class StatusConfigTests
{
    [Fact]
    public void ParsesHostIdAndBothStatusKeyForms()
    {
        var text = """
            project = "shop"
            image_registry = "registry.example.com"
            state_registry = "state.example.net"
            desired_repo = "shop/state/aws-us-east-1-amd64-1a2b3c4d/desired"
            status_repo = "shop/state/aws-us-east-1-amd64-1a2b3c4d/status"
            host_id = "i-0a1b2c3d4e5f67890"
            status_public_keys = ["__PUBLIC1__"]

            [status_keys]
            "__KEYID2__" = { public_key = "__PUBLIC2__", not_after = "2027-11-24T00:00:00Z" }

            [target]
            provider = "aws"
            region = "us-east-1"
            arch = "amd64"
            selector = { stack = "prod" }
            """
            .Replace("__PUBLIC1__", CloudArtifactTestData.PublicKey1Base64, StringComparison.Ordinal)
            .Replace("__PUBLIC2__", Convert.ToBase64String(CloudArtifactTestData.PublicKey2), StringComparison.Ordinal)
            .Replace("__KEYID2__", CloudArtifactTestData.KeyId2, StringComparison.Ordinal);

        var config = AgentConfigLoader.Parse(text);

        Assert.Equal("i-0a1b2c3d4e5f67890", config.HostId);
        Assert.Equal(2, config.StatusKeys.Keys.Count);
        Assert.True(config.StatusKeys.TryFind(CloudArtifactTestData.KeyId1, out _));
        Assert.True(config.StatusKeys.TryFindUnexpired(
            CloudArtifactTestData.KeyId2,
            new DateTimeOffset(2026, 11, 24, 18, 0, 0, TimeSpan.Zero),
            out var second));
        Assert.Equal(new DateTimeOffset(2027, 11, 24, 0, 0, 0, TimeSpan.Zero), second.NotAfter);
    }

    [Fact]
    public void RejectsAMismatchedStatusKeyId()
    {
        var text = """
            project = "shop"
            image_registry = "registry.example.com"
            desired_repo = "shop/desired"

            [status_keys]
            "__KEYID2__" = "__PUBLIC1__"

            [target]
            provider = "aws"
            region = "us-east-1"
            arch = "amd64"
            selector = { stack = "prod" }
            """
            .Replace("__PUBLIC1__", CloudArtifactTestData.PublicKey1Base64, StringComparison.Ordinal)
            .Replace("__KEYID2__", CloudArtifactTestData.KeyId2, StringComparison.Ordinal);

        var error = Assert.Throws<AgentConfigException>(() => AgentConfigLoader.Parse(text));
        Assert.Contains("key ID does not match", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnInvalidHostId()
    {
        var text = """
            project = "shop"
            image_registry = "registry.example.com"
            desired_repo = "shop/desired"
            host_id = "bad host"

            [target]
            provider = "aws"
            region = "us-east-1"
            arch = "amd64"
            selector = { stack = "prod" }
            """;

        var error = Assert.Throws<AgentConfigException>(() => AgentConfigLoader.Parse(text));
        Assert.Contains("host_id", error.Message, StringComparison.Ordinal);
    }
}

public class StatusCommandLineTests
{
    [Fact]
    public void ParsesStatusConfigAndJson()
    {
        var options = CommandLine.Parse(["status", "--config", "/etc/narwhal/agent.toml", "--target", "prod", "--json"]);

        Assert.Equal("status", options.Command);
        Assert.Equal("/etc/narwhal/agent.toml", options.Value("config"));
        Assert.Equal("prod", options.Value("target"));
        Assert.True(options.Json);
        Assert.Contains("status", CommandLine.Usage(), StringComparison.Ordinal);
        Assert.Contains("--watch", CommandLine.Usage(CommandLine.Find("status")!), StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesWatch()
    {
        var options = CommandLine.Parse(["status", "--watch"]);

        Assert.True(options.Flag("watch"));
        Assert.Equal("status", options.Command);
    }

    [Theory]
    [InlineData(ReconcilePhases.Idle, 0)]
    [InlineData(ReconcilePhases.Degraded, 3)]
    [InlineData(ReconcilePhases.Failed, 5)]
    [InlineData(ReconcilePhases.ReplayRejected, 5)]
    [InlineData(ReconcilePhases.Applying, 3)]
    [InlineData(ReconcilePhases.RegistryUnavailable, 3)]
    public void MapsPhasesToExitCodes(string phase, int expected)
    {
        Assert.Equal(expected, StatusCommand.ExitCode(phase));
    }
}
