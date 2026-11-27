using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Narwhal.Cloud;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

/// <summary>
/// Opt-in proof that the oras-backed client can push and read back a blob and a
/// signed-status manifest through a throwaway local registry. Set
/// <c>NARWHAL_ORAS_E2E=1</c> to run it; it self-skips when oras or podman is
/// missing, or when registry:2 cannot be pulled.
/// </summary>
[Collection(MultiPlatformIntegrationTests.CliProcessSerial.Name)]
public class OrasRegistryE2ETests
{
    [OrasE2EFact]
    public async Task PushesAndReadsBackABlobAndManifest()
    {
        var port = FreePort();
        var name = "narwhal-oras-e2e-" + Environment.ProcessId + "-" + port;
        var podman = Environment.GetEnvironmentVariable("NARWHAL_PODMAN") ?? "podman";

        RunOrFail(podman,
            ["run", "-d", "--rm", "--name", name, "-p", $"127.0.0.1:{port}:5000", "docker.io/library/registry:2"]);

        try
        {
            await WaitUntilReadyAsync(port, TimeSpan.FromSeconds(60));

            var repository = "narwhal/e2e/oras";
            using var client = new OciRegistryClient(
                $"127.0.0.1:{port}",
                new RegistryAuthConfig { Insecure = true });

            var payload = """{"schema":"narwhal.status/v1","phase":"idle"}"""u8.ToArray();
            var signature = "status-signature"u8.ToArray();
            var payloadDigest = CloudEncoding.Sha256Digest(payload);
            var signatureDigest = CloudEncoding.Sha256Digest(signature);

            await client.PushBlobAsync(repository, payload, CancellationToken.None);
            await client.PushBlobAsync(repository, signature, CancellationToken.None);
            await client.PushBlobAsync(repository, StatusManifest.EmptyConfig, CancellationToken.None);

            var manifest = StatusManifest.Build(
                payloadDigest,
                payload.Length,
                signatureDigest,
                signature.Length,
                3,
                fallback: false);
            var result = await client.PushManifestAsync(
                repository,
                StatusManifest.LatestTag,
                manifest,
                StatusManifest.ManifestMediaType,
                CancellationToken.None);

            Assert.Equal(CloudEncoding.Sha256Digest(manifest), result.Digest);
            Assert.True(await client.BlobExistsAsync(repository, payloadDigest, CancellationToken.None));

            var fetchedBlob = await client.GetBlobAsync(repository, payloadDigest, CancellationToken.None);
            Assert.Equal(payload, fetchedBlob);

            var fetched = await client.GetStatusManifestAsync(repository, StatusManifest.LatestTag, CancellationToken.None);
            Assert.Equal(CloudEncoding.Sha256Digest(manifest), fetched.Digest);
            Assert.Equal(manifest, fetched.Bytes);
            Assert.Equal(payloadDigest, fetched.Selection.Payload.Digest);
            Assert.Equal(signatureDigest, fetched.Selection.Signature.Digest);
        }
        finally
        {
            Run(podman, ["rm", "-f", name], TimeSpan.FromSeconds(30));
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilReadyAsync(int port, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/v2/");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                last = e;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"registry did not become ready: {last?.Message ?? "timed out"}");
    }

    private static void RunOrFail(string file, string[] arguments)
    {
        var result = Run(file, arguments, TimeSpan.FromSeconds(60));
        if (result is null)
            throw new InvalidOperationException($"could not run {file} {string.Join(' ', arguments)}");
        if (result.Value.ExitCode != 0)
            throw new InvalidOperationException(
                $"{file} {string.Join(' ', arguments)} exited {result.Value.ExitCode}: {result.Value.Stderr}");
    }

    private static (int ExitCode, string Stdout, string Stderr)? Run(string file, string[] arguments, TimeSpan timeout)
    {
        try
        {
            var start = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null)
                return null;

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return (process.ExitCode, stdout.Result.Trim(), stderr.Result.Trim());
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>Runs only when explicitly opted in and oras plus a local registry are available.</summary>
public sealed class OrasE2EFactAttribute : FactAttribute
{
    public OrasE2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("NARWHAL_ORAS_E2E") != "1")
        {
            Skip = "set NARWHAL_ORAS_E2E=1 to run the oras registry e2e";
            return;
        }

        var oras = Environment.GetEnvironmentVariable("NARWHAL_ORAS") ?? "oras";
        var orasPath = ResolveTool(oras);
        if (orasPath is null)
        {
            Skip = $"oras is not on PATH ({oras})";
            return;
        }
        if (!ToolExists(orasPath, ["version"]))
        {
            Skip = $"oras failed to report its version ({orasPath})";
            return;
        }

        var podman = Environment.GetEnvironmentVariable("NARWHAL_PODMAN") ?? "podman";
        var podmanPath = ResolveTool(podman);
        if (podmanPath is null)
        {
            Skip = $"podman is not on PATH ({podman})";
            return;
        }
        if (!ToolExists(podmanPath, ["--version"]))
        {
            Skip = $"podman failed to report its version ({podmanPath})";
            return;
        }

        if (!ImageExists(podmanPath, "registry:2") && !Pull(podmanPath))
        {
            Skip = "registry:2 is not available; the e2e needs the image or network access to pull it";
        }
    }

    private static string? ResolveTool(string name)
    {
        if (name.Contains('/', StringComparison.Ordinal))
            return File.Exists(name) ? name : null;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static bool ToolExists(string file, string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null)
                return false;
            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ImageExists(string podman, string image)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(podman, ["image", "exists", image])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return false;
            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Pull(string podman)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(podman, ["pull", "-q", "docker.io/library/registry:2"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return false;
            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            return process.WaitForExit(120_000) && process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
