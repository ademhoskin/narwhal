using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class BootPinTests
{
    [Fact]
    public void AcceptsAWellFormedPin()
    {
        var directory = ReconcileTestData.CreateBootPins(
            Path.Combine(Path.GetTempPath(), "narwhal-reconcile", Guid.NewGuid().ToString("n")));

        BootPins.Validate(directory);
    }

    [Fact]
    public void RejectsAMissingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        var error = Assert.Throws<AgentConfigException>(() => BootPins.Validate(directory));
        Assert.Contains("not found", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnEmptyDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-reconcile", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);

        var error = Assert.Throws<AgentConfigException>(() => BootPins.Validate(directory));
        Assert.Contains("empty", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAKeyIdThatDoesNotMatchTheFileOrKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-reconcile", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "0000000000000000.pub"),
            $"narwhal-ed25519-v1 {CloudArtifactTestData.PublicKey1Base64}\n");

        var error = Assert.Throws<AgentConfigException>(() => BootPins.Validate(directory));
        Assert.Contains("does not match", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAMalformedTimestamp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-reconcile", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, CloudArtifactTestData.KeyId1 + ".pub"),
            $"narwhal-ed25519-v1 {CloudArtifactTestData.PublicKey1Base64}\nnot_after=tomorrow\n");

        var error = Assert.Throws<AgentConfigException>(() => BootPins.Validate(directory));
        Assert.Contains("not_after", error.Message, StringComparison.Ordinal);
    }
}
