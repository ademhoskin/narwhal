using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using Narwhal.Nix;

namespace Narwhal.Oci;

/// <summary>Reads docker-archive metadata without asking podman.</summary>
public static class ArchiveImage
{
    /// <summary>
    /// Return the image id (<c>sha256:&lt;hex&gt;</c>) recorded in a docker-archive
    /// tarball, reading both plain <c>.tar</c> and gzip-compressed archives.
    /// </summary>
    /// <exception cref="BuildException">The archive has no readable manifest.</exception>
    public static string ReadImageId(string archivePath)
    {
        try
        {
            using var stream = Open(archivePath);
            using var reader = new TarReader(stream);
            while (reader.GetNextEntry() is { } entry)
            {
                if (!IsManifest(entry.Name))
                    continue;

                using var buffer = new MemoryStream();
                entry.DataStream?.CopyTo(buffer);
                buffer.Position = 0;

                using var document = JsonDocument.Parse(buffer);
                if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
                    throw new BuildException($"cannot read an image id from '{archivePath}'",
                        "manifest.json does not contain an image entry");

                var image = document.RootElement[0];
                if (image.ValueKind != JsonValueKind.Object)
                    throw new BuildException($"cannot read an image id from '{archivePath}'",
                        $"manifest.json entry is a {image.ValueKind}, expected an object");

                if (!image.TryGetProperty("Config", out var config) || config.ValueKind != JsonValueKind.String)
                    throw new BuildException($"cannot read an image id from '{archivePath}'",
                        "manifest.json has no Config entry");

                return "sha256:" + ConfigHex(config.GetString()!, archivePath);
            }
        }
        catch (InvalidDataException e)
        {
            throw new BuildException($"cannot read the container archive '{archivePath}'", e.Message);
        }
        catch (JsonException e)
        {
            throw new BuildException($"cannot read an image id from '{archivePath}'",
                $"manifest.json is not valid JSON: {e.Message}");
        }

        throw new BuildException($"cannot read an image id from '{archivePath}'",
            "the archive has no manifest.json; is it a docker-archive tarball?");
    }

    private static Stream Open(string archivePath)
    {
        var file = File.OpenRead(archivePath);
        try
        {
            if (!archivePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && !IsGzip(file))
                return file;

            file.Position = 0;
            return new GZipStream(file, CompressionMode.Decompress);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static bool IsGzip(Stream stream)
    {
        Span<byte> magic = stackalloc byte[2];
        var read = stream.Read(magic);
        stream.Position = 0;
        return read == 2 && magic[0] == 0x1f && magic[1] == 0x8b;
    }

    private static bool IsManifest(string name)
    {
        var normalized = name.TrimStart('.', '/');
        return normalized == "manifest.json";
    }

    private static string ConfigHex(string config, string archivePath)
    {
        var name = config.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];

        if (name.Length != 64 || !name.All(Uri.IsHexDigit))
            throw new BuildException($"cannot read an image id from '{archivePath}'",
                $"manifest.json Config '{config}' is not an image-id file name");

        return name.ToLowerInvariant();
    }
}
