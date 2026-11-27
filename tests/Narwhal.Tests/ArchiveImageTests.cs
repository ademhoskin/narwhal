using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Narwhal.Nix;
using Narwhal.Oci;
using Xunit;

namespace Narwhal.Tests;

public class ArchiveImageTests : IDisposable
{
    private const string Hex = "84b6d144e22d31525b453965399be530d03b0d9f0ade75a45d6d9a27b359c7b9";
    private readonly List<string> _directories = [];

    [Theory]
    [InlineData(".tar")]
    [InlineData(".tar.gz")]
    public void ReadsTheImageIdFromADockerArchive(string extension)
    {
        var path = WriteArchive(extension);

        Assert.Equal("sha256:" + Hex, ArchiveImage.ReadImageId(path));
    }

    [Fact]
    public void RejectsAnArchiveWithoutAManifest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        var path = Path.Combine(directory, "empty.tar");
        using (var file = File.Create(path))
        using (var writer = new TarWriter(file, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "other.txt")
            {
                DataStream = new MemoryStream("nope"u8.ToArray()),
            });
        }

        var error = Assert.Throws<BuildException>(() => ArchiveImage.ReadImageId(path));
        Assert.Contains("no manifest.json", error.Detail!);
    }

    [Fact]
    public void RejectsMalformedManifestJsonWithABuildError()
    {
        var path = WriteArchiveWithManifest(".tar", "{not json");

        // Given manifest.json is not valid JSON, the reader must not leak JsonException.
        var error = Assert.Throws<BuildException>(() => ArchiveImage.ReadImageId(path));
        Assert.Contains("manifest.json is not valid JSON", error.Detail!);
    }

    [Fact]
    public void RejectsANonObjectManifestEntry()
    {
        var path = WriteArchiveWithManifest(".tar", "[42]");

        // Given manifest[0] is not an object, the reader reports a BuildException.
        var error = Assert.Throws<BuildException>(() => ArchiveImage.ReadImageId(path));
        Assert.Contains("expected an object", error.Detail!);
    }

    private string WriteArchive(string extension)
    {
        return WriteArchiveWithManifest(
            extension,
            $$""" [{"Config":"{{Hex}}.json","RepoTags":["shop/web:latest"],"Layers":[]}] """);
    }

    private string WriteArchiveWithManifest(string extension, string manifestJson)
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        var path = Path.Combine(directory, "image" + extension);

        var manifest = Encoding.UTF8.GetBytes(manifestJson);

        using var file = File.Create(path);
        Stream archive = extension.EndsWith(".gz", StringComparison.Ordinal)
            ? new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true)
            : file;
        using (archive)
        using (var writer = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "manifest.json")
            {
                DataStream = new MemoryStream(manifest),
            });
        }

        return path;
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory must not fail the test run.
            }
        }

        GC.SuppressFinalize(this);
    }
}
