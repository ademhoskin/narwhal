using System.Text;
using Narwhal.Cloud;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class ReconcileSecretTests
{
    [Fact]
    public async Task TheVersionIdIsTheDigestOfTheResolvedContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-reconcile", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "password");
        File.WriteAllText(path, "first-secret");

        var resolver = new FileSecretResolver();
        var reference = new SecretRef { Source = "file", Id = path };
        var first = await resolver.ResolveAsync("PASSWORD", reference, "shop-api-PASSWORD", CancellationToken.None);

        Assert.Equal(
            CloudEncoding.Sha256Digest(Encoding.UTF8.GetBytes("first-secret")),
            first.VersionId);
        Assert.DoesNotContain("first-secret", first.VersionId, StringComparison.Ordinal);

        File.WriteAllText(path, "second-secret");
        var second = await resolver.ResolveAsync("PASSWORD", reference, "shop-api-PASSWORD", CancellationToken.None);

        Assert.NotEqual(first.VersionId, second.VersionId);
        Assert.Equal(
            CloudEncoding.Sha256Digest(Encoding.UTF8.GetBytes("second-secret")),
            second.VersionId);
        Assert.DoesNotContain("second-secret", second.VersionId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJsonKeyIsResolvedBeforeHashing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-reconcile", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "secrets.json");
        File.WriteAllText(path, """{"db":"keyed-secret","other":"ignored"}""");

        var resolver = new FileSecretResolver();
        var secret = await resolver.ResolveAsync(
            "PASSWORD",
            new SecretRef { Source = "file", Id = path, Key = "db" },
            "shop-api-PASSWORD",
            CancellationToken.None);

        Assert.Equal("keyed-secret", secret.Value);
        Assert.Equal(
            CloudEncoding.Sha256Digest(Encoding.UTF8.GetBytes("keyed-secret")),
            secret.VersionId);
    }
}
