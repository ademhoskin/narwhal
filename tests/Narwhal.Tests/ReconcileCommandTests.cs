using Narwhal.Cli;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class ReconcileCommandTests
{
    [Fact]
    public async Task AMissingConfigExitsOne()
    {
        var options = CommandLine.Parse([
            "reconcile",
            "--config",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "agent.toml"),
        ]);

        var exit = await ReconcileCommand.RunAsync(options, ReconcileTestData.Reporter(), CancellationToken.None);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task AnotherReconcileExitsZeroWithoutTouchingState()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var configPath = Path.Combine(directory, "agent.toml");
        var trustDirectory = ReconcileTestData.CreateBootPins(directory);
        File.WriteAllText(configPath, $$"""
            project = "shop"
            image_registry = "registry.example.com"
            desired_repo = "shop/state/target/desired"
            lock_timeout_seconds = 0
            state_dir = "{{directory}}"
            trust_dir = "{{trustDirectory}}"

            [target]
            provider = "aws"
            region = "us-east-1"
            arch = "amd64"
            selector = { stack = "prod" }
            """);

        var store = new ReconcileStore(directory);
        using var held = await store.TryAcquireLockAsync(0, CancellationToken.None);
        Assert.NotNull(held);

        var options = CommandLine.Parse(["reconcile", "--config", configPath]);
        var exit = await ReconcileCommand.RunAsync(options, ReconcileTestData.Reporter(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(store.StatePath));
    }
}
