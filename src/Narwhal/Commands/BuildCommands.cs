using System.Text.Json;
using Narwhal.Cli;
using Narwhal.Config;
using Narwhal.Nix;
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

        if (services.Count == 0)
        {
            ctx.Reporter.Ok("no buildable services in the manifest");
            return 0;
        }

        var built = new List<BuildOutcome>();
        foreach (var service in services)
        {
            var existing = ctx.Lock.Service(service.Name);
            var image = await ctx.Nix.BuildImageAsync(ctx.Manifest, service, cancellationToken, rebuild);

            if (locked && existing is null)
                throw new ReproducibilityException(
                    $"service '{service.Name}' is missing from {Path.GetFileName(ctx.Lock.Path)}\nrun 'narwhal build' once to record it");

            if (locked && existing is not null && existing.NarHash.Length > 0 && existing.NarHash != image.NarHash)
                throw new ReproducibilityException(
                    $"'{service.Name}' did not reproduce from the locked inputs\n  locked: {existing.NarHash}\n  built:  {image.NarHash}\n" +
                    "either an input moved or the build is not reproducible; run 'narwhal build' to accept the new output");

            var imageId = existing?.ImageId ?? string.Empty;
            if (!noLoad)
            {
                imageId = await ctx.Podman.LoadImageAsync(image.ArtifactPath, service.Reference(ctx.Manifest), cancellationToken);
                if (locked && existing is not null && existing.ImageId.Length > 0 && existing.ImageId != imageId)
                    throw new ReproducibilityException(
                        $"'{service.Name}' produced a different image id\n  locked: {existing.ImageId}\n  loaded: {imageId}");
            }

            built.Add(new BuildOutcome(service, image, imageId));
        }

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

        var failures = 0;
        var rows = new List<IReadOnlyList<string>>();
        var json = new List<(string Service, bool Ok, string Detail)>();

        foreach (var service in services)
        {
            var locked = ctx.Lock.Service(service.Name);
            if (locked is null)
            {
                failures++;
                rows.Add([service.Name, "missing", "-", "-"]);
                json.Add((service.Name, false, "missing from lock"));
                continue;
            }

            try
            {
                var image = await ctx.Nix.BuildImageAsync(ctx.Manifest, service, cancellationToken);
                var narOk = locked.NarHash.Length > 0 && locked.NarHash == image.NarHash;

                var idOk = true;
                var loadedId = "-";
                if (!noLoad)
                {
                    loadedId = await ctx.Podman.LoadImageAsync(image.ArtifactPath, service.Reference(ctx.Manifest), cancellationToken);
                    idOk = locked.ImageId.Length == 0 || locked.ImageId == loadedId;
                }

                var ok = narOk && idOk;
                if (!ok)
                    failures++;

                rows.Add([service.Name, ok ? "match" : "MISMATCH",
                    narOk ? Short(locked.NarHash) : $"{Short(locked.NarHash)} != {Short(image.NarHash)}",
                    noLoad ? "-" : idOk ? Short(locked.ImageId) : $"{Short(locked.ImageId)} != {Short(loadedId)}"]);
                json.Add((service.Name, ok, ok ? "match" : "mismatch"));
            }
            catch (BuildException e)
            {
                failures++;
                rows.Add([service.Name, "build failed", e.Detail?.Split('\n').FirstOrDefault() ?? e.Message, "-"]);
                json.Add((service.Name, false, "build failed"));
            }
        }

        if (reporter.Json)
        {
            await WriteJsonAsync(reporter, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("project", ctx.Manifest.Name);
                writer.WriteBoolean("ok", failures == 0);
                writer.WriteStartArray("services");
                foreach (var (service, ok, detail) in json)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", service);
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
            reporter.Table(["service", "result", "nar hash", "image id"], rows);
        }

        if (failures > 0)
            throw new ReproducibilityException($"{failures} service(s) failed verification");

        reporter.Ok("every service reproduced from its locked inputs");
        return 0;
    }

    public static async Task<int> PlanAsync(CommandContext ctx, CliOptions options, CancellationToken cancellationToken)
    {
        var reporter = ctx.Reporter;
        var manifest = ctx.Manifest;
        var stale = ctx.Lock.Services.Count > 0 && ctx.Lock.ManifestSha256 != manifest.ContentHash;

        var rows = new List<IReadOnlyList<string>>();
        foreach (var service in manifest.Ordered())
        {
            var locked = ctx.Lock.Service(service.Name);
            var status = service.Prebuilt ? "prebuilt"
                : locked is null ? "new"
                : locked.Attr != service.AttrPath(manifest) ? "stale"
                : stale ? "stale"
                : "locked";

            var digest = locked is null ? "-"
                : locked.ImageId.Length > 0 ? Short(locked.ImageId)
                : Short(locked.NarHash);

            rows.Add([service.Name, service.Prebuilt ? "-" : service.AttrPath(manifest),
                service.Reference(manifest), status, digest]);
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
                writer.WriteString("system", manifest.Build.System);
                writer.WriteStartArray("services");
                foreach (var service in manifest.Ordered())
                {
                    var locked = ctx.Lock.Service(service.Name);
                    writer.WriteStartObject();
                    writer.WriteString("name", service.Name);
                    writer.WriteBoolean("prebuilt", service.Prebuilt);
                    writer.WriteString("attr", service.Prebuilt ? "" : service.AttrPath(manifest));
                    writer.WriteString("image", service.Reference(manifest));
                    writer.WriteString("locked_nar_hash", locked?.NarHash);
                    writer.WriteString("locked_image_id", locked?.ImageId);
                    writer.WriteString("store_path", locked?.StorePath);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
            return 0;
        }

        reporter.Step($"{manifest.Name} ({manifest.Services.Count} service(s), {manifest.Build.System})");
        reporter.Table(["service", "attr", "image", "status", "digest"], rows);

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
        foreach (var service in services)
        {
            var reference = service.Reference(ctx.Manifest);
            if (!await ctx.Podman.ImageExistsAsync(reference, cancellationToken))
            {
                var locked = ctx.Lock.Service(service.Name);
                if (locked is null || !File.Exists(locked.StorePath))
                    throw new PodmanException($"image '{reference}' is not loaded; run 'narwhal build' first");

                var artifact = NixClient.ResolveArtifact(locked.StorePath);
                await ctx.Podman.LoadImageAsync(artifact, reference, cancellationToken);
            }

            await ctx.Podman.PushAsync(reference, cancellationToken);
        }

        ctx.Reporter.Ok($"pushed {services.Count} image(s)");
        return 0;
    }

    /// <summary>
    /// Reuse the lock when it describes the current build, otherwise build, for `up`.
    /// Returns image ids keyed by service name.
    /// </summary>
    public static async Task<Dictionary<string, string>> EnsureImagesAsync(CommandContext ctx,
        IReadOnlyList<ServiceSpec> services, bool rebuild, CancellationToken cancellationToken)
    {
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

            var locked = ctx.Lock.Service(service.Name);
            var reusable = !rebuild
                && locked is not null
                && locked.Attr == service.AttrPath(ctx.Manifest)
                && locked.NarHash.Length > 0
                && File.Exists(locked.StorePath);

            if (reusable)
            {
                var artifact = NixClient.ResolveArtifact(locked!.StorePath);
                var imageId = await ctx.Podman.LoadImageAsync(artifact, service.Reference(ctx.Manifest), cancellationToken);
                if (locked.ImageId.Length > 0 && locked.ImageId != imageId)
                    ctx.Reporter.Warn($"'{service.Name}' loaded as {Short(imageId)} but the lock records {Short(locked.ImageId)}");

                ids[service.Name] = imageId;
                built.Add(new BuildOutcome(service,
                    new BuiltImage(locked.Attr, locked.StorePath, artifact, locked.NarHash), imageId));
                continue;
            }

            var image = await ctx.Nix.BuildImageAsync(ctx.Manifest, service, cancellationToken, rebuild);
            var loadedId = await ctx.Podman.LoadImageAsync(image.ArtifactPath, service.Reference(ctx.Manifest), cancellationToken);
            ids[service.Name] = loadedId;
            built.Add(new BuildOutcome(service, image, loadedId));
            lockChanged = true;
        }

        if (lockChanged || ctx.Lock.Services.Count == 0 || ctx.Lock.ManifestSha256 != ctx.Manifest.ContentHash)
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
            ctx.Lock.Services[outcome.Service.Name] = new LockedService
            {
                Attr = outcome.Image.Attr,
                Flake = outcome.Service.FlakeRef(ctx.Manifest),
                Image = outcome.Service.Repository(ctx.Manifest),
                Tag = outcome.Service.Tag,
                StorePath = outcome.Image.StorePath,
                NarHash = outcome.Image.NarHash,
                ImageId = outcome.ImageId,
            };
        }

        // Drop services that no longer exist in the manifest.
        foreach (var name in ctx.Lock.Services.Keys.Where(name => ctx.Manifest.Find(name) is null).ToList())
            ctx.Lock.Services.Remove(name);

        ctx.Lock.Narwhal = App.Version;

        // Only claim the manifest hash when the lock actually covers it.
        var complete = ctx.Manifest.Services
            .Where(s => !s.Prebuilt)
            .All(s =>
            {
                var entry = ctx.Lock.Service(s.Name);
                return entry is not null && entry.Attr == s.AttrPath(ctx.Manifest) && entry.NarHash.Length > 0;
            });

        if (complete)
            ctx.Lock.ManifestSha256 = ctx.Manifest.ContentHash;

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
                    writer.WriteStartObject();
                    writer.WriteString("name", outcome.Service.Name);
                    writer.WriteString("attr", outcome.Image.Attr);
                    writer.WriteString("image", outcome.Service.Reference(ctx.Manifest));
                    writer.WriteString("store_path", outcome.Image.StorePath);
                    writer.WriteString("nar_hash", outcome.Image.NarHash);
                    writer.WriteString("image_id", outcome.ImageId);
                    writer.WriteString("loaded", noLoad ? "skipped" : "podman");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }).GetAwaiter().GetResult();
            return;
        }

        foreach (var outcome in built)
        {
            reporter.Ok($"{outcome.Service.Name} {outcome.Image.NarHash} {(noLoad ? "(not loaded)" : Short(outcome.ImageId))}");
            reporter.Detail(outcome.Image.StorePath);
        }
    }

    private sealed record BuildOutcome(ServiceSpec Service, BuiltImage Image, string ImageId);

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
