using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Narwhal.Cloud;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class AgentImageSourceTests
{
    private static readonly byte[] Binary = Encoding.UTF8.GetBytes("#!/bin/sh\necho narwhal\n");

    [Fact]
    public async Task ExtractsNarwhalFromAGzipTarLayer()
    {
        var tar = Tar("usr/local/bin/narwhal", Binary);
        var blob = Gzip(tar);
        var source = Source(blob, "application/vnd.oci.image.layer.v1.tar+gzip");

        var binary = await source.FetchBinaryAsync(Spec(), CancellationToken.None);

        Assert.Equal(Binary, binary);
    }

    [Fact]
    public async Task ExtractsNarwhalFromAPlainTarLayer()
    {
        var blob = Tar("narwhal", Binary);
        var source = Source(blob, RegistryAgentImageSource.TarMediaType);

        var binary = await source.FetchBinaryAsync(Spec(), CancellationToken.None);

        Assert.Equal(Binary, binary);
    }

    [Fact]
    public async Task AcceptsAnExplicitRawBinaryLayer()
    {
        var source = Source(Binary, RegistryAgentImageSource.BinaryMediaType);

        var binary = await source.FetchBinaryAsync(Spec(), CancellationToken.None);

        Assert.Equal(Binary, binary);
    }

    [Fact]
    public async Task RejectsAnArchiveWithoutNarwhal()
    {
        var blob = Tar("usr/local/bin/other", Binary);
        var source = Source(blob, RegistryAgentImageSource.TarMediaType);

        var error = await Assert.ThrowsAsync<CloudArtifactException>(() =>
            source.FetchBinaryAsync(Spec(), CancellationToken.None));

        Assert.Equal(CloudArtifactErrorCodes.LayerInvalid, error.Code);
    }

    [Fact]
    public async Task RejectsAnUnrecognizedLayerMediaType()
    {
        var source = Source(Binary, "application/octet-stream");

        var error = await Assert.ThrowsAsync<CloudArtifactException>(() =>
            source.FetchBinaryAsync(Spec(), CancellationToken.None));

        Assert.Equal(CloudArtifactErrorCodes.LayerInvalid, error.Code);
    }

    [Fact]
    public async Task RejectsAManifestThatDoesNotMatchThePinnedDigest()
    {
        var source = Source(Binary, RegistryAgentImageSource.BinaryMediaType, manifestDigest: "sha256:" + new string('a', 64));

        var error = await Assert.ThrowsAsync<CloudArtifactException>(() =>
            source.FetchBinaryAsync(Spec(), CancellationToken.None));

        Assert.Equal(CloudArtifactErrorCodes.DigestMismatch, error.Code);
    }

    [Fact]
    public async Task RejectsAnArchiveWithMoreThanOneNarwhal()
    {
        var tar = Tar("a/narwhal", Binary, "b/narwhal", Binary);
        var source = Source(tar, RegistryAgentImageSource.TarMediaType);

        var error = await Assert.ThrowsAsync<CloudArtifactException>(() =>
            source.FetchBinaryAsync(Spec(), CancellationToken.None));

        Assert.Equal(CloudArtifactErrorCodes.LayerInvalid, error.Code);
    }

    [Fact]
    public async Task SelectsTheSingleLayerThatContainsNarwhal()
    {
        var baseLayer = Tar("etc/os-release", Encoding.UTF8.GetBytes("narwhal\n"));
        var agentLayer = Tar("usr/local/bin/narwhal", Binary);
        var source = Source(
            [baseLayer, agentLayer],
            [RegistryAgentImageSource.TarMediaType, RegistryAgentImageSource.TarMediaType]);

        var binary = await source.FetchBinaryAsync(Spec(), CancellationToken.None);

        Assert.Equal(Binary, binary);
    }

    [Fact]
    public async Task RejectsNarwhalInMoreThanOneLayer()
    {
        var source = Source(
            [Tar("a/narwhal", Binary), Tar("b/narwhal", Binary)],
            [RegistryAgentImageSource.TarMediaType, RegistryAgentImageSource.TarMediaType]);

        var error = await Assert.ThrowsAsync<CloudArtifactException>(() =>
            source.FetchBinaryAsync(Spec(), CancellationToken.None));

        Assert.Equal(CloudArtifactErrorCodes.LayerInvalid, error.Code);
        Assert.Contains("more than one", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAMalformedGzipLayer()
    {
        var source = Source(Encoding.UTF8.GetBytes("not gzip"), RegistryAgentImageSource.TarGzipMediaType);

        var error = await Assert.ThrowsAsync<CloudArtifactException>(() =>
            source.FetchBinaryAsync(Spec(), CancellationToken.None));

        Assert.Equal(CloudArtifactErrorCodes.LayerInvalid, error.Code);
    }

    private static RegistryAgentImageSource Source(
        byte[] blob,
        string mediaType,
        string? manifestDigest = null)
        => Source([blob], [mediaType], manifestDigest);

    private static RegistryAgentImageSource Source(
        IReadOnlyList<byte[]> blobs,
        IReadOnlyList<string> mediaTypes,
        string? manifestDigest = null)
    {
        var layers = blobs
            .Select((blob, index) => new ArtifactLayer(
                mediaTypes[index],
                CloudEncoding.Sha256Digest(blob),
                blob.Length))
            .ToList();
        var digest = manifestDigest ?? "sha256:" + new string('d', 64);
        var manifest = new RegistryManifest(
            digest,
            [],
            layers,
            new SelectedArtifactLayers(layers[0], layers[0]));
        var contents = layers
            .Select((layer, index) => (layer.Digest, Blob: blobs[index]))
            .ToDictionary(pair => pair.Digest, pair => pair.Blob, StringComparer.Ordinal);
        return new RegistryAgentImageSource(new FakeRegistry(manifest, contents));
    }

    private static AgentSpec Spec() => new()
    {
        Version = "0.4.0",
        Repository = "registry.example.com/shop/narwhal",
        Digest = "sha256:" + new string('d', 64),
        BinarySha256 = Convert.ToHexStringLower(SHA256.HashData(Binary)),
    };

    private static byte[] Tar(params object[] entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, leaveOpen: true))
        {
            for (var index = 0; index < entries.Length; index += 2)
            {
                var data = (byte[])entries[index + 1];
                var entry = new PaxTarEntry(TarEntryType.RegularFile, (string)entries[index])
                {
                    DataStream = new MemoryStream(data),
                };
                writer.WriteEntry(entry);
            }
        }

        return stream.ToArray();
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(bytes);
        return output.ToArray();
    }

    private sealed class FakeRegistry(RegistryManifest manifest, IReadOnlyDictionary<string, byte[]> blobs) : IRegistryClient
    {
        public Task<RegistryManifest> GetManifestAsync(string repository, string reference, CancellationToken cancellationToken) =>
            Task.FromResult(manifest);

        public Task<byte[]> GetBlobAsync(string repository, string digest, CancellationToken cancellationToken) =>
            Task.FromResult(blobs[digest]);
    }
}
