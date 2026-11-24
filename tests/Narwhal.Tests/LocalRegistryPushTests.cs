using System.Diagnostics;
using Xunit;

namespace Narwhal.Tests;

/// <summary>
/// Opt-in proof that a multi-platform push publishes a two-entry OCI index to a
/// real registry. Set <c>NARWHAL_E2E_REGISTRY=1</c> to run it; it self-skips
/// when podman, curl, or jq is missing, or when the registry:2 image cannot be
/// pulled (the check needs a loopback registry and network access once).
/// </summary>
[Collection(MultiPlatformIntegrationTests.ProcessCollection.Name)]
public class LocalRegistryPushTests
{
    [OptInRegistryFact]
    public async Task LocalRegistryPushPublishesATwoEntryOciIndex()
    {
        var script = Path.Combine(RepoRoot(), "tests", "e2e", "local-registry-push.sh");
        Assert.True(File.Exists(script), $"missing e2e script at {script}");

        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);
        start.Environment["NARWHAL_DLL"] = typeof(App).Assembly.Location;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("could not start bash");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process);
            throw new TimeoutException("local-registry e2e did not finish within ten minutes");
        }

        var output = await stdout;
        var errors = await stderr;
        Assert.True(process.ExitCode == 0,
            $"e2e script exited {process.ExitCode}\nstdout:\n{output}\nstderr:\n{errors}");
        Assert.Contains("E2E OK", output);
        Assert.Contains("entries: amd64,arm64", output);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "flake.nix")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("could not find the repository root (flake.nix)");
    }

    /// <summary>Let the script's EXIT trap clean up before the fallback kill.</summary>
    private static async Task TerminateAsync(Process process)
    {
        if (!process.HasExited)
        {
            try
            {
                var start = new ProcessStartInfo("kill")
                {
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                start.ArgumentList.Add("-TERM");
                start.ArgumentList.Add(process.Id.ToString());
                using var kill = Process.Start(start);
                kill?.WaitForExit(5000);
            }
            catch (Exception)
            {
                // Fall through to the hard kill below.
            }
        }

        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
        }
    }
}

/// <summary>Runs only when explicitly opted in and its host tools exist.</summary>
public sealed class OptInRegistryFactAttribute : FactAttribute
{
    public OptInRegistryFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("NARWHAL_E2E_REGISTRY") != "1")
        {
            Skip = "set NARWHAL_E2E_REGISTRY=1 to run the local-registry e2e";
            return;
        }

        var podman = Environment.GetEnvironmentVariable("NARWHAL_PODMAN") ?? "podman";
        foreach (var tool in new[] { podman, "curl", "jq" })
        {
            if (!ToolExists(tool))
            {
                Skip = $"{tool} is not available";
                return;
            }
        }

        var host = Environment.GetEnvironmentVariable("NARWHAL_SYSTEM");
        if (string.IsNullOrWhiteSpace(host))
            host = RunCapture("uname", ["-m"], TimeSpan.FromSeconds(5)) ?? "unknown";
        if (NormalizeHostSystem(host) is null)
        {
            Skip = $"host system '{host.Trim()}' is not supported; the e2e needs x86_64-linux or aarch64-linux";
            return;
        }

        if (!RegistryImageAvailable())
            Skip = "registry:2 is not available; the e2e needs the image or network access to pull it";
    }

    private static string? NormalizeHostSystem(string host) => host.Trim() switch
    {
        "x86_64" or "x86_64-linux" or "amd64" => "x86_64-linux",
        "aarch64" or "arm64" or "aarch64-linux" => "aarch64-linux",
        _ => null,
    };

    private static bool ToolExists(string name)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(name, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return false;
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool RegistryImageAvailable()
    {
        var podman = Environment.GetEnvironmentVariable("NARWHAL_PODMAN") ?? "podman";
        return Run(podman, ["image", "exists", "registry:2"], TimeSpan.FromSeconds(10))
            || Run(podman, ["pull", "-q", "docker.io/library/registry:2"], TimeSpan.FromSeconds(60));
    }

    private static bool Run(string file, string[] arguments, TimeSpan timeout)
        => RunCapture(file, arguments, timeout) is not null;

    private static string? RunCapture(string file, string[] arguments, TimeSpan timeout)
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

            _ = stderr.Result;
            return process.ExitCode == 0 ? stdout.Result.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
