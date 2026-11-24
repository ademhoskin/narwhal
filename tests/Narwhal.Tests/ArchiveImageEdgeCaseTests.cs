using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Narwhal.Nix;
using Narwhal.Oci;
using Xunit;

namespace Narwhal.Tests;

/// <summary>Archive id extraction for the docker-archive shapes nix and podman produce.</summary>
public class ArchiveImageEdgeCaseTests : IDisposable
{
    private const string Hex = "84b6d144e22d31525b453965399be530d03b0d9f0ade75a45d6d9a27b359c7b9";
    private readonly List<string> _directories = [];

    [Theory]
    [InlineData("image.tar", false)]
    [InlineData("image.tar.gz", true)]
    public void ReadsAManifestEntryWithALeadingDotSlash(string fileName, bool gzip)
    {
        var path = WriteArchive(fileName, gzip,
            ("./manifest.json", Manifest($"\"{Hex}.json\"")));

        Assert.Equal("sha256:" + Hex, ArchiveImage.ReadImageId(path));
    }

    [Fact]
    public void DetectsGzipByMagicEvenWithoutTheGzExtension()
    {
        var path = WriteArchive("image.tar", gzip: true, ("manifest.json", Manifest($"\"{Hex}.json\"")));

        Assert.Equal("sha256:" + Hex, ArchiveImage.ReadImageId(path));
    }

    [Fact]
    public void SkipsEarlierEntriesUntilTheManifest()
    {
        var path = WriteArchive("image.tar", gzip: false,
            ("blobs/sha256/abc", "not the manifest"),
            ("manifest.json", Manifest($"\"{Hex}.json\"")));

        Assert.Equal("sha256:" + Hex, ArchiveImage.ReadImageId(path));
    }

    [Fact]
    public void ReadsAConfigPathThatCarriesDirectories()
    {
        var path = WriteArchive("image.tar", gzip: false,
            ("manifest.json", Manifest($"\"blobs/sha256/{Hex}.json\"")));

        Assert.Equal("sha256:" + Hex, ArchiveImage.ReadImageId(path));
    }

    [Fact]
    public void ReadsAWindowsStyleConfigPath()
    {
        var path = WriteArchive("image.tar", gzip: false,
            ("manifest.json", Manifest($"\"blobs\\\\{Hex}.json\"")));

        Assert.Equal("sha256:" + Hex, ArchiveImage.ReadImageId(path));
    }

    [Fact]
    public void LowercasesAnUppercaseConfigName()
    {
        var path = WriteArchive("image.tar", gzip: false,
            ("manifest.json", Manifest($"\"{Hex.ToUpperInvariant()}.json\"")));

        Assert.Equal("sha256:" + Hex, ArchiveImage.ReadImageId(path));
    }

    [Fact]
    public void RejectsAnEmptyManifestArray()
    {
        var path = WriteArchive("image.tar", gzip: false, ("manifest.json", "[]"));

        var error = Assert.Throws<BuildException>(() => ArchiveImage.ReadImageId(path));
        Assert.Contains("does not contain an image entry", error.Detail!);
    }

    [Fact]
    public void RejectsAnEntryWithoutConfig()
    {
        var path = WriteArchive("image.tar", gzip: false,
            ("manifest.json", """[{"RepoTags":["shop/web:latest"],"Layers":[]}]"""));

        var error = Assert.Throws<BuildException>(() => ArchiveImage.ReadImageId(path));
        Assert.Contains("no Config entry", error.Detail!);
    }

    [Fact]
    public void RejectsAConfigThatIsNotAString()
    {
        var path = WriteArchive("image.tar", gzip: false,
            ("manifest.json", """[{"Config":42}]"""));

        var error = Assert.Throws<BuildException>(() => ArchiveImage.ReadImageId(path));
        Assert.Contains("no Config entry", error.Detail!);
    }

    [Theory]
    [InlineData("abc.json")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz.json")]
    public void RejectsAConfigNameThatIsNotA64CharHexId(string config)
    {
        var path = WriteArchive("image.tar", gzip: false,
            ("manifest.json", Manifest($"\"{config}\"")));

        var error = Assert.Throws<BuildException>(() => ArchiveImage.ReadImageId(path));
        Assert.Contains("is not an image-id file name", error.Detail!);
        Assert.Contains(config, error.Detail!);
    }

    private static string Manifest(string config) =>
        $$"""[{"Config":{{config}},"RepoTags":["shop/web:latest"],"Layers":[]}]""";

    private string WriteArchive(string fileName, bool gzip,
        params (string Name, string Content)[] entries)
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        var path = Path.Combine(directory, fileName);

        using var file = File.Create(path);
        Stream archive = gzip
            ? new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true)
            : file;
        using (archive)
        using (var writer = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                });
            }
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
    }
}
