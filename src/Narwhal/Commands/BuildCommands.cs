using System.Text.Json;
using Narwhal.Cli;
using Narwhal.Config;
using Narwhal.Nix;
using Narwhal.Oci;
using Narwhal.Podman;

namespace Narwhal.Commands;

/// <summary>build, verify, plan and push: everything that touches nix-built images.</summary>
public static class BuildCommands
{
    public static async Task<int> BuildAsync(CommandContext ctx, CliOptions options, CancellationToken cancellationToken)
    {
        var locked = options.Flag("locked") || options.Flag("frozen");
        var noLoad = options.Flag("no-load");
        var rebuild = options.Flag("rebuild");
        var services = ctx.Select(options.Positionals, includePrebuilt: false);
        var systems = ctx.SelectSystems(options);

        if (services.Count == 0)
        {
            ctx.Reporter.Ok("no buildable services in the manifest");
            return 0;
        }

        var failures = new List<string>();
        var built = new List<BuildOutcome>();
        foreach (var service in services)
        {
            foreach (var system in systems)
            {
                var existing = ctx.Lock.Platform(service.Name, system);

                BuiltImage image;
                try
                {
                    image = await ctx.Nix.BuildImageAsync(ctx.Manifest, service, system, cancellationToken, rebuild);
                }
                catch (BuildException e) when (locked)
                {
                    failures.Add($"'{service.Name}' ({system}): build failed: {FirstLine(e)}");
                    continue;
                }

                var imageId = ArchiveImage.ReadImageId(image.ArtifactPath);

                if (locked)
                {
                    if (existing is null)
                    {
                        failures.Add($"'{service.Name}' ({system}) is missing from {Path.GetFileName(ctx.Lock.Path)}");
                    }
                    else
                    {
                        if (existing.NarHash.Length > 0 && existing.NarHash != image.NarHash)
                            failures.Add($"'{service.Name}' ({system}) did not reproduce: locked nar hash {existing.NarHash}, built {image.NarHash}");
                        if (existing.ImageId.Length > 0 && !SameId(existing.ImageId, imageId))
                            failures.Add($"'{service.Name}' ({system}) produced a different image id: locked {existing.ImageId}, built {imageId}");
                    }
                }

                if (!noLoad && system == ctx.HostSystem)
                {
                    var loadedId = await ctx.Podman.LoadImageAsync(image.ArtifactPath, service.Reference(ctx.Manifest), cancellationToken);
                    if (!SameId(loadedId, imageId))
                        ctx.Reporter.Warn($"'{service.Name}' ({system}) loaded as {Short(loadedId)} but the archive records {Short(imageId)}");
                }

                built.Add(new BuildOutcome(service, image, imageId));
            }
        }

        if (locked && failures.Count > 0)
            throw new ReproducibilityException(
                $"{failures.Count} image(s) did not match the lock:\n" + string.Join("\n", failures.Select(f => "  " + f)));

        if (!locked)
        {
            await UpdateLockAsync(ctx, built, cancellationToken);
            ctx.Lock.Save();
            ctx.Reporter.Ok($"lock written to {Path.GetRelativePath(Environment.CurrentDirectory, ctx.Lock.Path)}");
        }

        ReportBuilt(ctx, built, noLoad);
        return 0;
    }

    public static async Task<int> VerifyAsync(CommandContext ctx, CliOptions options, CancellationToken cancellationToken)
    {
        var noLoad = options.Flag("no-load");
        var services = ctx.Select(options.Positionals, includePrebuilt: false);
        var systems = ctx.SelectSystems(options);
        var reporter = ctx.Reporter;

        if (services.Count == 0)
        {
            reporter.Ok("no buildable services in the manifest");
            return 0;
        }

        if (ctx.Lock.Services.Count == 0)
            throw new ReproducibilityException($"{Path.GetFileName(ctx.Lock.Path)} is missing; run 'narwhal build' first");

        if (ctx.Lock.ManifestSha256 != ctx.Manifest.ContentHash)
            reporter.Warn($"{Path.GetFileName(ctx.Manifest.Path)} changed since the lock was written; verifying the locked image attributes only");

        var failures = new List<string>();
        var rows = new List<IReadOnlyList<string>>();
        var json = new List<(string Service, string System, string Platform, bool Ok, string Detail)>();

        foreach (var service in services)
        {
            foreach (var system in systems)
            {
                var platform = NixPlatform.ToOciPlatform(system);
                var locked = ctx.Lock.Platform(service.Name, system);
                if (locked is null)
                {
                    failures.Add($"{service.Name}/{system}");
                    rows.Add([service.Name, system, "missing", "-", "-"]);
                    json.Add((service.Name, system, platform, false, "missing from lock"));
                    continue;
                }

                try
                {
                    var image = await ctx.Nix.BuildImageAsync(ctx.Manifest, service, system, cancellationToken);
                    var imageId = ArchiveImage.ReadImageId(image.ArtifactPath);
                    var narOk = locked.NarHash.Length > 0 && locked.NarHash == image.NarHash;

                    if (!noLoad && system == ctx.HostSystem)
                    {
                        var loadedId = await ctx.Podman.LoadImageAsync(image.ArtifactPath, service.Reference(ctx.Manifest), cancellationToken);
                        if (!SameId(loadedId, imageId))
                            reporter.Warn($"'{service.Name}' ({system}) loaded as {Short(loadedId)} but the archive records {Short(imageId)}");
                    }

                    var idOk = locked.ImageId.Length == 0 || SameId(locked.ImageId, imageId);
                    var ok = narOk && idOk;
                    if (!ok)
                        failures.Add($"{service.Name}/{system}");

                    rows.Add([service.Name, system, ok ? "match" : "MISMATCH",
                        narOk ? Short(locked.NarHash) : $"{Short(locked.NarHash)} != {Short(image.NarHash)}",
                        idOk ? Short(locked.ImageId) : $"{Short(locked.ImageId)} != {Short(imageId)}"]);
                    json.Add((service.Name, system, platform, ok, ok ? "match" : "mismatch"));
                }
                catch (Exception e) when (e is BuildException or PodmanException)
                {
                    failures.Add($"{service.Name}/{system}");
                    rows.Add([service.Name, system, "build failed", FirstLine(e), "-"]);
                    json.Add((service.Name, system, platform, false, "build failed: " + FirstLine(e)));
                }
            }
        }

        if (reporter.Json)
        {
            await WriteJsonAsync(reporter, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("project", ctx.Manifest.Name);
                writer.WriteBoolean("ok", failures.Count == 0);
                writer.WriteStartArray("services");
                foreach (var (service, system, platform, ok, detail) in json)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", service);
                    writer.WriteString("system", system);
                    writer.WriteString("platform", platform);
                    writer.WriteBoolean("ok", ok);
                    writer.WriteString("detail", detail);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
        }
        else
        {
            reporter.Step("verifying locked images");
            reporter.Table(["service", "system", "result", "nar hash", "image id"], rows);
        }

        if (failures.Count > 0)
            throw new ReproducibilityException(
                $"{failures.Count} service/system pair(s) failed verification: {string.Join(", ", failures)}");

        reporter.Ok("every service reproduced from its locked inputs");
        return 0;
    }

    public static async Task<int> PlanAsync(CommandContext ctx, CliOptions options, CancellationToken cancellationToken)
    {
        var reporter = ctx.Reporter;
        var manifest = ctx.Manifest;
        var systems = manifest.Build.Systems;
        var stale = ctx.Lock.Services.Count > 0 && ctx.Lock.ManifestSha256 != manifest.ContentHash;

        var rows = new List<IReadOnlyList<string>>();
        foreach (var service in manifest.Ordered())
        {
            if (service.Prebuilt)
            {
                rows.Add([service.Name, "-", "-", service.Reference(manifest), "prebuilt", "-"]);
                continue;
            }

            foreach (var system in systems)
            {
                var lockedService = ctx.Lock.Service(service.Name);
                var locked = lockedService?.Platforms.TryGetValue(system, out var entry) == true ? entry : null;
                var attr = service.AttrPath(manifest, system);
                var status = lockedService is null ? "new"
                    : locked is null ? "missing-platform"
                    : locked.Attr != attr ? "stale"
                    : stale ? "stale"
                    : "locked";

                var digest = locked is null ? "-"
                    : locked.ImageId.Length > 0 ? Short(locked.ImageId)
                    : Short(locked.NarHash);

                rows.Add([service.Name, system, attr, service.Reference(manifest), status, digest]);
            }
        }

        if (reporter.Json)
        {
            await WriteJsonAsync(reporter, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("project", manifest.Name);
                writer.WriteString("manifest", manifest.Path);
                writer.WriteString("lock", manifest.LockPath);
                writer.WriteBoolean("lock_stale", stale);
                writer.WriteStartArray("systems");
                foreach (var system in systems)
                    writer.WriteStringValue(system);
                writer.WriteEndArray();
                writer.WriteStartArray("services");
                foreach (var service in manifest.Ordered())
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", service.Name);
                    writer.WriteBoolean("prebuilt", service.Prebuilt);
                    writer.WriteString("image", service.Reference(manifest));
                    writer.WriteStartObject("platforms");
                    if (!service.Prebuilt)
                    {
                        var lockedService = ctx.Lock.Service(service.Name);
                        foreach (var system in systems)
                        {
                            var locked = lockedService?.Platforms.TryGetValue(system, out var entry) == true ? entry : null;
                            var attr = service.AttrPath(manifest, system);
                            var status = lockedService is null ? "new"
                                : locked is null ? "missing-platform"
                                : locked.Attr != attr ? "stale"
                                : stale ? "stale"
                                : "locked";

                            writer.WriteStartObject(system);
                            writer.WriteString("platform", NixPlatform.ToOciPlatform(system));
                            writer.WriteString("attr", attr);
                            writer.WriteString("status", status);
                            writer.WriteString("digest", locked is null ? "-"
                                : locked.ImageId.Length > 0 ? Short(locked.ImageId)
                                : Short(locked.NarHash));
                            writer.WriteString("locked_nar_hash", locked?.NarHash);
                            writer.WriteString("locked_image_id", locked?.ImageId);
                            writer.WriteString("store_path", locked?.StorePath);
                            writer.WriteEndObject();
                        }
                    }
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
            return 0;
        }

        reporter.Step($"{manifest.Name} ({manifest.Services.Count} service(s), systems: {string.Join(", ", systems)})");
        reporter.Table(["service", "system", "attr", "image", "status", "digest"], rows);

        if (stale)
            reporter.Warn($"{Path.GetFileName(manifest.Path)} changed since the lock was written; run 'narwhal build' to refresh it");
        if (ctx.Lock.Services.Count == 0)
            reporter.Info("no lock yet: 'narwhal build' will pin image hashes into " + Path.GetFileName(manifest.LockPath));

        if (options.Flag("refresh"))
        {
            var flakes = manifest.Services
                .Where(s => !s.Prebuilt)
                .Select(s => s.FlakeRef(manifest))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var flake in flakes)
            {
                var metadata = await ctx.Nix.MetadataAsync(flake, manifest.Directory, cancellationToken);
                reporter.Info($"flake {flake}: {metadata.Url} {(metadata.Revision is null ? "" : "@" + ShortRevision(metadata.Revision))}");
                reporter.Info($"  narHash {metadata.NarHash ?? "unknown"}");
            }
        }

        return 0;
    }

    public static async Task<int> PushAsync(CommandContext ctx, CliOptions options, CancellationToken cancellationToken)
    {
        IReadOnlyList<ServiceSpec> requested = options.Positionals.Count == 0
            ? []
            : ctx.Select(options.Positionals);
        foreach (var service in requested.Where(s => s.Prebuilt))
            throw new UsageException($"service '{service.Name}' is prebuilt; narwhal does not push images it did not build");

        var services = ctx.Select(options.Positionals, includePrebuilt: false);
        var systems = ctx.SelectSystems(options);

        foreach (var service in services)
        {
            var reference = service.Reference(ctx.Manifest);
            var previousId = await ctx.Podman.ImageIdAsync(reference, cancellationToken);

            try
            {
                if (systems.Count == 1)
                    await PushPlatformAsync(ctx, service, reference, systems[0], cancellationToken);
                else
                    await PushIndexAsync(ctx, service, reference, systems, cancellationToken);
            }
            finally
            {
                await RestoreHostTagAsync(ctx, service, reference, previousId, cancellationToken);
            }
        }

        ctx.Reporter.Ok($"pushed {services.Count} image(s)");
        return 0;
    }

    /// <summary>Push one platform's locked image id.</summary>
    private static async Task PushPlatformAsync(CommandContext ctx, ServiceSpec service, string reference,
        string system, CancellationToken cancellationToken)
    {
        var locked = RequireLockedPlatform(ctx, service, system);

        if (StorePathExists(locked.StorePath))
        {
            var artifact = NixClient.ResolveArtifact(locked.StorePath);
            var archiveId = ArchiveImage.ReadImageId(artifact);
            RequireLockedImageId(service, system, locked, archiveId);

            var current = await ctx.Podman.ImageIdAsync(reference, cancellationToken);
            if (current is null || !SameId(current, archiveId))
            {
                var loaded = await ctx.Podman.LoadImageAsync(artifact, reference, cancellationToken);
                if (!SameId(loaded, archiveId))
                    throw new PodmanException(
                        $"'{service.Name}' ({system}) loaded as {loaded} but the archive records {archiveId}");
            }

            // Push the id, not the tag: the tag may belong to another platform.
            await ctx.Podman.PushByIdAsync(archiveId, reference, cancellationToken);
            return;
        }

        // The archive was collected: the lock can still identify an already-loaded image.
        if (locked.ImageId.Length == 0)
            throw new PodmanException(
                $"'{service.Name}' ({system}) has no archive on disk and no locked image id; run 'narwhal build' first");

        var existing = await ctx.Podman.ImageIdAsync(reference, cancellationToken);
        if (existing is null || !SameId(existing, locked.ImageId))
            throw new PodmanException(
                $"'{service.Name}' ({system}) archive is gone and the tag does not hold the locked image; run 'narwhal build' first");

        await ctx.Podman.PushByIdAsync(locked.ImageId, reference, cancellationToken);
    }

    private static async Task PushIndexAsync(CommandContext ctx, ServiceSpec service, string reference,
        IReadOnlyList<string> systems, CancellationToken cancellationToken)
    {
        var index = ManifestIndexName(ctx.Manifest, service, reference);
        var ids = new List<string>();
        foreach (var system in systems)
        {
            var locked = RequireLockedPlatform(ctx, service, system);
            if (!StorePathExists(locked.StorePath))
            {
                if (locked.ImageId.Length == 0)
                    throw new PodmanException(
                        $"'{service.Name}' ({system}) has no archive on disk and no locked image id; run 'narwhal build' first");

                var existing = await ctx.Podman.ImageIdAsync(locked.ImageId, cancellationToken);
                if (existing is null || !SameId(existing, locked.ImageId))
                    throw new PodmanException(
                        $"'{service.Name}' ({system}) archive is gone and its locked image is not loaded; run 'narwhal build' first");

                ids.Add(locked.ImageId);
                continue;
            }

            var artifact = NixClient.ResolveArtifact(locked.StorePath);
            var archiveId = ArchiveImage.ReadImageId(artifact);
            RequireLockedImageId(service, system, locked, archiveId);

            var loaded = await ctx.Podman.LoadImageAsync(artifact, reference, cancellationToken);
            if (!SameId(loaded, archiveId))
                throw new PodmanException(
                    $"'{service.Name}' ({system}) loaded as {loaded} but the archive records {archiveId}");

            ids.Add(archiveId);
        }

        // Start from an empty index so a previous failed push cannot keep stale platforms.
        await ctx.Podman.RemoveManifestAsync(index, cancellationToken);
        await ctx.Podman.CreateManifestAsync(index, ids, cancellationToken);
        await ctx.Podman.PushManifestAsync(index, reference, cancellationToken);
    }

    private static LockedPlatform RequireLockedPlatform(CommandContext ctx, ServiceSpec service, string system)
    {
        var locked = ctx.Lock.Platform(service.Name, system)
            ?? throw new PodmanException($"'{service.Name}' has no locked image for {system}; run 'narwhal build' first");

        var attr = service.AttrPath(ctx.Manifest, system);
        if (locked.Attr != attr)
            throw new PodmanException(
                $"'{service.Name}' ({system}) lock attr is '{locked.Attr}' but the manifest expects '{attr}'; run 'narwhal build' first");

        return locked;
    }

    private static void RequireLockedImageId(ServiceSpec service, string system,
        LockedPlatform locked, string archiveId)
    {
        if (locked.ImageId.Length > 0 && !SameId(locked.ImageId, archiveId))
            throw new PodmanException(
                $"'{service.Name}' ({system}) lock records image {locked.ImageId} but the archive is {archiveId}; run 'narwhal build' first");
    }

    /// <summary>Leave the tag on the host platform when configured, else restore its previous owner.</summary>
    private static async Task RestoreHostTagAsync(CommandContext ctx, ServiceSpec service, string reference,
        string? previousId, CancellationToken cancellationToken)
    {
        try
        {
            if (ctx.Manifest.Build.Systems.Contains(ctx.HostSystem, StringComparer.Ordinal))
            {
                var host = ctx.Lock.Platform(service.Name, ctx.HostSystem);
                if (host is not null && StorePathExists(host.StorePath))
                {
                    var expectedAttr = service.AttrPath(ctx.Manifest, ctx.HostSystem);
                    var expectedPlatform = NixPlatform.ToOciPlatform(ctx.HostSystem);
                    if (host.Attr != expectedAttr || host.Platform != expectedPlatform)
                    {
                        ctx.Reporter.Warn(
                            $"host lock entry for '{service.Name}' ({ctx.HostSystem}) does not match the manifest; restoring the previous tag");
                    }
                    else
                    {
                        var artifact = NixClient.ResolveArtifact(host.StorePath);
                        var archiveId = ArchiveImage.ReadImageId(artifact);
                        var current = await ctx.Podman.ImageIdAsync(reference, cancellationToken);
                        if (current is not null && SameId(current, archiveId))
                            return;

                        var loaded = await ctx.Podman.LoadImageAsync(artifact, reference, cancellationToken);
                        if (SameId(loaded, archiveId))
                            return;

                        ctx.Reporter.Warn(
                            $"host image for '{service.Name}' loaded as {loaded} but the archive records {archiveId}; restoring the previous tag");
                    }
                }
            }

            if (previousId is not null)
            {
                await ctx.Podman.TagAsync(previousId, reference, cancellationToken);
                return;
            }

            ctx.Reporter.Warn($"'{reference}' is left on the last loaded platform; add {ctx.HostSystem} to build.systems to keep it on the host");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Reporter.Warn($"could not restore '{reference}' to the host platform: {e.Message}");
        }
    }

    /// <summary>
    /// Reuse the lock when it describes the host build, otherwise build the host
    /// platform, for `up`. Returns image ids keyed by service name.
    /// </summary>
    public static async Task<Dictionary<string, string>> EnsureImagesAsync(CommandContext ctx,
        IReadOnlyList<ServiceSpec> services, bool rebuild, CancellationToken cancellationToken)
    {
        var host = ctx.HostSystem;
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var built = new List<BuildOutcome>();
        var lockChanged = false;

        foreach (var service in services)
        {
            if (service.Prebuilt)
            {
                var reference = service.Reference(ctx.Manifest);
                if (!await ctx.Podman.ImageExistsAsync(reference, cancellationToken))
                    await ctx.Podman.PullAsync(reference, cancellationToken);
                ids[service.Name] = await ctx.Podman.ImageIdAsync(reference, cancellationToken) ?? reference;
                continue;
            }

            var attr = service.AttrPath(ctx.Manifest, host);
            var locked = ctx.Lock.Platform(service.Name, host);
            var reusable = !rebuild
                && locked is not null
                && locked.Attr == attr
                && locked.NarHash.Length > 0
                && StorePathExists(locked.StorePath);

            if (reusable)
            {
                var artifact = NixClient.ResolveArtifact(locked!.StorePath);
                var archiveId = ArchiveImage.ReadImageId(artifact);
                var loadedId = await ctx.Podman.LoadImageAsync(artifact, service.Reference(ctx.Manifest), cancellationToken);
                if (!SameId(loadedId, archiveId))
                    ctx.Reporter.Warn($"'{service.Name}' loaded as {Short(loadedId)} but the archive records {Short(archiveId)}");
                if (locked.ImageId.Length > 0 && !SameId(locked.ImageId, archiveId))
                    ctx.Reporter.Warn($"'{service.Name}' archive is {Short(archiveId)} but the lock records {Short(locked.ImageId)}");

                ids[service.Name] = loadedId;
                built.Add(new BuildOutcome(service,
                    new BuiltImage(host, NixPlatform.ToOciPlatform(host), locked.Attr, locked.StorePath, artifact, locked.NarHash),
                    archiveId));
                continue;
            }

            var image = await ctx.Nix.BuildImageAsync(ctx.Manifest, service, host, cancellationToken, rebuild);
            var imageId = ArchiveImage.ReadImageId(image.ArtifactPath);
            var hostLoadedId = await ctx.Podman.LoadImageAsync(image.ArtifactPath, service.Reference(ctx.Manifest), cancellationToken);
            if (!SameId(hostLoadedId, imageId))
                ctx.Reporter.Warn($"'{service.Name}' loaded as {Short(hostLoadedId)} but the archive records {Short(imageId)}");

            ids[service.Name] = hostLoadedId;
            built.Add(new BuildOutcome(service, image, imageId));
            lockChanged = true;
        }

        if (lockChanged || ctx.Lock.NeedsMigration || ctx.Lock.Services.Count == 0
            || ctx.Lock.ManifestSha256 != ctx.Manifest.ContentHash)
        {
            await UpdateLockAsync(ctx, built, cancellationToken);
            ctx.Lock.Save();
        }

        return ids;
    }

    private static async Task UpdateLockAsync(CommandContext ctx, IReadOnlyList<BuildOutcome> built,
        CancellationToken cancellationToken)
    {
        foreach (var outcome in built)
        {
            if (!ctx.Lock.Services.TryGetValue(outcome.Service.Name, out var entry))
            {
                entry = new LockedService();
                ctx.Lock.Services[outcome.Service.Name] = entry;
            }

            entry.Flake = outcome.Service.FlakeRef(ctx.Manifest);
            entry.Image = outcome.Service.Repository(ctx.Manifest);
            entry.Tag = outcome.Service.Tag;
            entry.Platforms[outcome.Image.System] = new LockedPlatform
            {
                Platform = outcome.Image.OciPlatform,
                Attr = outcome.Image.Attr,
                StorePath = outcome.Image.StorePath,
                NarHash = outcome.Image.NarHash,
                ImageId = outcome.ImageId,
            };
        }

        // Drop services that no longer exist in the manifest.
        foreach (var name in ctx.Lock.Services.Keys.Where(name => ctx.Manifest.Find(name) is null).ToList())
            ctx.Lock.Services.Remove(name);

        ctx.Lock.Narwhal = App.Version;

        // Only claim the manifest hash when the lock covers every configured system.
        if (ctx.Lock.Covers(ctx.Manifest))
            ctx.Lock.ManifestSha256 = ctx.Manifest.ContentHash;
        else
            ctx.Lock.ManifestSha256 = "";

        await UpdateFlakesAsync(ctx, cancellationToken);
    }

    private static async Task UpdateFlakesAsync(CommandContext ctx, CancellationToken cancellationToken)
    {
        var flakes = ctx.Manifest.Services
            .Where(s => !s.Prebuilt)
            .Select(s => s.FlakeRef(ctx.Manifest))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        ctx.Lock.Flakes.Clear();
        foreach (var flake in flakes)
        {
            try
            {
                var metadata = await ctx.Nix.MetadataAsync(flake, ctx.Manifest.Directory, cancellationToken);
                ctx.Lock.Flakes[flake] = new LockedFlake
                {
                    Url = metadata.Url,
                    NarHash = metadata.NarHash,
                    Revision = metadata.Revision,
                    LastModified = metadata.LastModified,
                };
            }
            catch (Exception e) when (e is BuildException or System.Text.Json.JsonException)
            {
                ctx.Reporter.Warn($"could not lock flake '{flake}': {e.Message}");
            }
        }
    }

    private static void ReportBuilt(CommandContext ctx, IReadOnlyList<BuildOutcome> built, bool noLoad)
    {
        var reporter = ctx.Reporter;
        if (reporter.Json)
        {
            WriteJsonAsync(reporter, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("project", ctx.Manifest.Name);
                writer.WriteStartArray("services");
                foreach (var outcome in built)
                {
                    var loaded = outcome.Image.System != ctx.HostSystem ? "foreign"
                        : noLoad ? "skipped"
                        : "podman";
                    writer.WriteStartObject();
                    writer.WriteString("name", outcome.Service.Name);
                    writer.WriteString("system", outcome.Image.System);
                    writer.WriteString("platform", outcome.Image.OciPlatform);
                    writer.WriteString("attr", outcome.Image.Attr);
                    writer.WriteString("image", outcome.Service.Reference(ctx.Manifest));
                    writer.WriteString("store_path", outcome.Image.StorePath);
                    writer.WriteString("nar_hash", outcome.Image.NarHash);
                    writer.WriteString("image_id", outcome.ImageId);
                    writer.WriteString("loaded", loaded);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }).GetAwaiter().GetResult();
            return;
        }

        foreach (var outcome in built)
        {
            var loaded = outcome.Image.System != ctx.HostSystem ? "(not loaded: foreign platform)"
                : noLoad ? "(not loaded)"
                : Short(outcome.ImageId);
            reporter.Ok($"{outcome.Service.Name} {outcome.Image.System} {outcome.Image.NarHash} {loaded}");
            reporter.Detail(outcome.Image.StorePath);
        }
    }

    private sealed record BuildOutcome(ServiceSpec Service, BuiltImage Image, string ImageId);

    private static string FirstLine(Exception e) =>
        e.Message.Split('\n')[0];

    private static bool StorePathExists(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private static string ManifestIndexName(Manifest manifest, ServiceSpec service, string reference)
    {
        var hash = Hashing.Sha256Hex(reference)[..12];
        return $"localhost/narwhal-index/{manifest.Name}/{service.Name}-{hash}:{service.Tag}";
    }

    private static bool SameId(string left, string right) =>
        string.Equals(NormalizeId(left), NormalizeId(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeId(string id) =>
        id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id;

    private static string Short(string value)
    {
        var hash = value.StartsWith("sha256:", StringComparison.Ordinal) ? value[7..] : value;
        return hash.Length <= 12 ? hash : hash[..12];
    }

    private static string ShortRevision(string revision) => revision.Length <= 8 ? revision : revision[..8];

    public static Task WriteJsonAsync(Reporter reporter, Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            write(writer);
        reporter.Data(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        return Task.CompletedTask;
    }
}
