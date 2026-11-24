using Narwhal.Config;
using Xunit;

namespace Narwhal.Tests;

/// <summary>The attr contract: one deterministic flake attribute per service and system.</summary>
public class AttrPathTests
{
    [Fact]
    public void DefaultAttrIsPrefixSystemName()
    {
        var manifest = ManifestWith(new BuildOptions { Systems = ["x86_64-linux"] },
            new ServiceSpec { Name = "api" });

        Assert.Equal("narwhal.images.x86_64-linux.api", manifest.Services[0].AttrPath(manifest, "x86_64-linux"));
    }

    [Fact]
    public void DefaultAttrHonoursACustomPrefix()
    {
        var manifest = ManifestWith(
            new BuildOptions { Systems = ["aarch64-linux"], AttrPrefix = "images.containers" },
            new ServiceSpec { Name = "api" });

        Assert.Equal("images.containers.aarch64-linux.api", manifest.Services[0].AttrPath(manifest, "aarch64-linux"));
    }

    [Theory]
    [InlineData("x86_64-linux", "images.x86_64-linux.api")]
    [InlineData("aarch64-linux", "images.aarch64-linux.api")]
    [InlineData("i686-linux", "images.i686-linux.api")]
    public void SubstitutesTheSystemPlaceholder(string system, string expected)
    {
        var manifest = ManifestWith(
            new BuildOptions { Systems = ["x86_64-linux", "aarch64-linux", "i686-linux"] },
            new ServiceSpec { Name = "api", Attr = "images.{system}.api" });

        Assert.Equal(expected, manifest.Services[0].AttrPath(manifest, system));
    }

    [Fact]
    public void SubstitutesEveryPlaceholderOccurrence()
    {
        var manifest = ManifestWith(
            new BuildOptions { Systems = ["x86_64-linux", "aarch64-linux"] },
            new ServiceSpec { Name = "api", Attr = "images.{system}.src-{system}.api" });

        Assert.Equal("images.aarch64-linux.src-aarch64-linux.api",
            manifest.Services[0].AttrPath(manifest, "aarch64-linux"));
    }

    [Fact]
    public void SubstitutesEvenWhenOnlyOneSystemIsConfigured()
    {
        var manifest = ManifestWith(
            new BuildOptions { Systems = ["x86_64-linux"] },
            new ServiceSpec { Name = "api", Attr = "images.{system}.api" });

        Assert.Equal("images.x86_64-linux.api", manifest.Services[0].AttrPath(manifest, "x86_64-linux"));
    }

    [Fact]
    public void PlainAttrIsAllowedForASingleSystem()
    {
        var manifest = ManifestWith(
            new BuildOptions { Systems = ["x86_64-linux"] },
            new ServiceSpec { Name = "api", Attr = "images.api" });

        Assert.Equal("images.api", manifest.Services[0].AttrPath(manifest, "x86_64-linux"));
    }

    [Fact]
    public void PlainAttrWithMultipleSystemsNamesTheServiceAndTheFix()
    {
        var manifest = ManifestWith(
            new BuildOptions { Systems = ["x86_64-linux", "aarch64-linux"] },
            new ServiceSpec { Name = "api", Attr = "images.api" });

        var error = Assert.Throws<ManifestException>(() => manifest.Services[0].AttrPath(manifest, "aarch64-linux"));

        Assert.Contains("service 'api'", error.Message);
        Assert.Contains("images.api", error.Message);
        Assert.Contains("2 systems", error.Message);
        Assert.Contains("{system}", error.Message);
    }

    [Fact]
    public void TheHostSystemIsNotSpecialWhenThePlaceholderIsMissing()
    {
        var manifest = ManifestWith(
            new BuildOptions { Systems = [NixPlatform.ContainerSystem, ForeignSystem()] },
            new ServiceSpec { Name = "api", Attr = "images.api" });

        Assert.Throws<ManifestException>(() =>
            manifest.Services[0].AttrPath(manifest, NixPlatform.ContainerSystem));
    }

    [Fact]
    public void AttrPathWithoutASystemUsesThePrimarySystem()
    {
        var manifest = ManifestWith(
            new BuildOptions { Systems = ["x86_64-linux", "aarch64-linux"] },
            new ServiceSpec { Name = "api" });

        var expected = $"{manifest.Build.AttrPrefix}.{manifest.Build.PrimarySystem}.{manifest.Services[0].Name}";
        Assert.Equal(expected, manifest.Services[0].AttrPath(manifest));
    }

    private static string ForeignSystem() =>
        NixPlatform.ContainerSystem == "x86_64-linux" ? "aarch64-linux" : "x86_64-linux";

    private static Manifest ManifestWith(BuildOptions build, params ServiceSpec[] services) => new()
    {
        Name = "attrs",
        Directory = "/tmp/attrs",
        Path = "/tmp/attrs/narwhal.toml",
        RawText = "",
        Build = build,
        Services = services.ToList(),
    };
}
