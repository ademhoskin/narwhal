using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class AgentConfigTests
{
    private const string Sample = """
        project = "shop"
        image_registry = "registry.example.com"
        state_registry = "state.example.net"
        desired_repo = "shop/state/aws-us-east-1-amd64-1a2b3c4d/desired"
        status_repo = "shop/state/aws-us-east-1-amd64-1a2b3c4d/status"
        poll_seconds = 30
        lock_timeout_seconds = 120
        mode = "timer"
        trust_dir = "/etc/narwhal/trust"
        state_dir = "/var/lib/narwhal"
        status_key = "/etc/narwhal/status-key"

        [target]
        provider = "aws"
        region = "us-east-1"
        arch = "amd64"
        selector = { stack = "prod" }

        [registry_auth]
        username = "agent"
        password = "secret"
        insecure = true
        """;

    [Fact]
    public void ParsesTheDocumentedShape()
    {
        var config = AgentConfigLoader.Parse(Sample);

        Assert.Equal("shop", config.Project);
        Assert.Equal("registry.example.com", config.ImageRegistry);
        Assert.Equal("state.example.net", config.StateRegistry);
        Assert.Equal(ReconcileTestData.Desired, config.DesiredRepository);
        Assert.Equal("shop/state/aws-us-east-1-amd64-1a2b3c4d/status", config.StatusRepository);
        Assert.Equal(30, config.PollSeconds);
        Assert.Equal(120, config.LockTimeoutSeconds);
        Assert.Equal("timer", config.Mode);
        Assert.Equal("/etc/narwhal/trust", config.TrustDirectory);
        Assert.Equal("/var/lib/narwhal", config.StateDirectory);
        Assert.Equal("/etc/narwhal/status-key", config.StatusKeyPath);
        Assert.Equal("aws", config.Target.Provider);
        Assert.Equal("prod", config.Target.Selector["stack"]);
        Assert.Equal("agent", config.Auth.Username);
        Assert.True(config.Auth.Insecure);
        Assert.Equal("shop", config.Expectation.Project);
    }

    [Fact]
    public void AcceptsAgentFieldsAfterTheTargetTable()
    {
        var config = AgentConfigLoader.Parse("""
            project = "shop"
            image_registry = "registry.example.com"
            desired_repo = "shop/desired"

            [target]
            provider = "gcp"
            region = "us-central1"
            arch = "arm64"
            selector = { stack = "prod" }
            mode = "timer"
            trust_dir = "/etc/narwhal/trust"
            state_dir = "/var/lib/narwhal"
            status_key = "/etc/narwhal/status-key"
            """);

        Assert.Equal("timer", config.Mode);
        Assert.Equal("/var/lib/narwhal", config.StateDirectory);
    }

    [Fact]
    public void TargetLogRedactOverridesTheRootArray()
    {
        var root = Sample.Replace(
            "lock_timeout_seconds = 120",
            "lock_timeout_seconds = 120\nlog_redact = [\"root-secret\"]",
            StringComparison.Ordinal);
        var rootConfig = AgentConfigLoader.Parse(root);
        Assert.Equal(["root-secret"], rootConfig.LogRedactPatterns);

        var target = root.Replace(
            "selector = { stack = \"prod\" }",
            "selector = { stack = \"prod\" }\nlog_redact = [\"target-secret\"]",
            StringComparison.Ordinal);
        var targetConfig = AgentConfigLoader.Parse(target);
        Assert.Equal(["target-secret"], targetConfig.LogRedactPatterns);

        var empty = target.Replace(
            "log_redact = [\"target-secret\"]",
            "log_redact = []",
            StringComparison.Ordinal);
        Assert.Empty(AgentConfigLoader.Parse(empty).LogRedactPatterns);
    }

    [Theory]
    [InlineData("project = \"shop\"", "flavor = true", "unknown key")]
    [InlineData("project = \"shop\"", "project = \"SHOP\"", "project")]
    [InlineData("desired_repo = \"shop/state/aws-us-east-1-amd64-1a2b3c4d/desired\"", "desired_repo = \"other/state/x/desired\"", "must start with")]
    [InlineData("desired_repo = \"shop/state/aws-us-east-1-amd64-1a2b3c4d/desired\"", "desired_repo = \"shop/desired:tag\"", "without a tag")]
    [InlineData("poll_seconds = 30", "poll_seconds = 0", "poll_seconds")]
    [InlineData("state_dir = \"/var/lib/narwhal\"", "state_dir = \"relative\"", "absolute path")]
    [InlineData("image_registry = \"registry.example.com\"", "image_registry = \"https://registry.example.com\"", "no scheme")]
    public void RejectsInvalidValues(string original, string replacement, string expected)
    {
        var text = Sample.Replace(original, replacement, StringComparison.Ordinal);
        var error = Assert.Throws<AgentConfigException>(() => AgentConfigLoader.Parse(text));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnUnknownTargetKey()
    {
        var text = Sample.Replace("arch = \"amd64\"", "arch = \"amd64\"\nflavor = \"x\"", StringComparison.Ordinal);
        var error = Assert.Throws<AgentConfigException>(() => AgentConfigLoader.Parse(text));
        Assert.Contains("target.flavor", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAMissingTarget()
    {
        var text = Sample[..Sample.IndexOf("[target]", StringComparison.Ordinal)];
        var error = Assert.Throws<AgentConfigException>(() => AgentConfigLoader.Parse(text));
        Assert.Contains("target", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesDistinctStateAndImageRegistryAuth()
    {
        var config = AgentConfigLoader.Parse("""
            project = "shop"
            image_registry = "registry.example.com"
            desired_repo = "shop/desired"

            [target]
            provider = "aws"
            region = "us-east-1"
            arch = "amd64"
            selector = { stack = "prod" }

            [registry_auth]
            token = "state-token"

            [image_registry_auth]
            username = "image-user"
            password = "image-password"
            """);

        Assert.Equal("state-token", config.Auth.Token);
        Assert.False(config.Auth.PasswordFromFile);
        Assert.Equal("image-user", config.ImageRegistryAuth.Username);
        Assert.Equal("image-password", config.ImageRegistryAuth.Password);
        Assert.True(config.ImageRegistryAuth.PasswordFromFile);
    }

    [Fact]
    public void ReusesStateRegistryAuthWhenTheRegistriesAreEqual()
    {
        var text = Sample.Replace(
            "image_registry = \"registry.example.com\"",
            "image_registry = \"state.example.net\"",
            StringComparison.Ordinal);

        var config = AgentConfigLoader.Parse(text);

        Assert.Equal("agent", config.ImageRegistryAuth.Username);
        Assert.Equal("secret", config.ImageRegistryAuth.Password);
        Assert.True(config.ImageRegistryAuth.PasswordFromFile);
    }

    [Fact]
    public void DoesNotReuseStateRegistryAuthForADifferentRegistry()
    {
        var config = AgentConfigLoader.Parse(Sample);

        Assert.Null(config.ImageRegistryAuth.Username);
        Assert.Null(config.ImageRegistryAuth.Password);
    }

    [Fact]
    public void ReusesStateRegistryAuthWhenStateRegistryIsOmitted()
    {
        var text = Sample.Replace("state_registry = \"state.example.net\"\n", "", StringComparison.Ordinal);

        var config = AgentConfigLoader.Parse(text);

        Assert.Null(config.StateRegistry);
        Assert.Equal("agent", config.ImageRegistryAuth.Username);
        Assert.Equal("secret", config.ImageRegistryAuth.Password);
    }

    [Fact]
    public void RequiresMode0600WhenPlaintextPasswordsAreInTheFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(Path.GetTempPath(), "narwhal-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "agent.toml");
        File.WriteAllText(path, Sample);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var error = Assert.Throws<AgentConfigException>(() => AgentConfigLoader.Load(path));
        Assert.Contains("mode 0600", error.Message, StringComparison.Ordinal);

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var config = AgentConfigLoader.Load(path);
        Assert.Equal("shop", config.Project);
    }

    [Fact]
    public void RequiresMode0600WhenAPlaintextTokenIsInTheFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var text = Sample.Replace(
            "[registry_auth]\nusername = \"agent\"\npassword = \"secret\"\ninsecure = true",
            "[registry_auth]\ntoken = \"file-token\"",
            StringComparison.Ordinal);
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "agent.toml");
        File.WriteAllText(path, text);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        var error = Assert.Throws<AgentConfigException>(() => AgentConfigLoader.Load(path));
        Assert.Contains("mode 0600", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsAMissingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "agent.toml");
        var error = Assert.Throws<AgentConfigException>(() => AgentConfigLoader.Load(path));
        Assert.Contains("agent config not found", error.Message, StringComparison.Ordinal);
    }
}
