using System.Diagnostics;
using Xunit;

namespace Narwhal.Tests;

/// <summary>
/// Opt-in proof that two concurrent pushes for one project/service/reference
/// publish the same complete OCI index a serialized push does. Set
/// <c>NARWHAL_E2E_REGISTRY=1</c> to run it; it self-skips when podman, curl, or
/// jq is missing, when registry:2 cannot be pulled, or on an unsupported host.
/// </summary>
[Collection(MultiPlatformIntegrationTests.ProcessCollection.Name)]
public class ConcurrentPushTests
{
    [OptInConcurrentPushFact]
    public async Task ConcurrentPushesPublishOneCompleteIndex()
    {
        var script = Path.Combine(RepoRoot(), "tests", "e2e", "local-registry-concurrent-push.sh");
        Assert.True(File.Exists(script), $"missing e2e script at {script}");

        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);
        start.Environment["NARWHAL_DLL"] = typeof(App).Assembly.Location;
        start.Environment["NARWHAL_E2E_REGISTRY_PULLED_BY_ATTRIBUTE"] =
            OptInRegistryFactAttribute.RegistryImagePulled ? "1" : "0";

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("could not start bash");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process);
            throw new TimeoutException("concurrent-push e2e did not finish within fifteen minutes");
        }

        var output = await stdout;
        var errors = await stderr;

        // xUnit.net 2.x has no runtime skip, so a 77 after the preflight
        // passed is a real problem and must be visible.
        Assert.True(process.ExitCode != 77,
            $"e2e reported a late skip (77) after the preflight passed\nstdout:\n{output}\nstderr:\n{errors}");

        Assert.True(process.ExitCode == 0,
            $"e2e script exited {process.ExitCode}\nstdout:\n{output}\nstderr:\n{errors}");
        Assert.DoesNotContain("image not known", output);
        Assert.DoesNotContain("image not known", errors);
        Assert.Contains("E2E concurrent-a: exit=0", output);
        Assert.Contains("E2E concurrent-b: exit=0", output);
        Assert.Contains("E2E concurrent window:", output);
        Assert.Contains("(overlap)", output);
        Assert.Contains("E2E concurrent index entries: amd64,arm64", output);
        Assert.Contains("E2E concurrent digest matches serialized push", output);
        Assert.Contains("E2E cleanup: no leaked container, images, or manifest lists", output);
        Assert.Contains("E2E OK", output);
    }

    internal static string RepoRoot()
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

/// <summary>
/// Runs the script's preflight so a runtime skip (for example a registry pull
/// that is unavailable) is reported as a skip instead of a failed test.
/// </summary>
public sealed class OptInConcurrentPushFactAttribute : OptInRegistryFactAttribute
{
    protected override void Validate()
    {
        var script = Path.Combine(ConcurrentPushTests.RepoRoot(),
            "tests", "e2e", "local-registry-concurrent-push.sh");
        if (!File.Exists(script))
        {
            SkipAfterCleanup($"missing e2e script at {script}");
            return;
        }

        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--check");
        start.Environment["NARWHAL_DLL"] = typeof(App).Assembly.Location;

        using var process = StartPreflight(start);
        if (process is null)
        {
            SkipAfterCleanup("could not start bash for the e2e preflight");
            return;
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180_000))
        {
            process.Kill(entireProcessTree: true);
            SkipAfterCleanup("concurrent-push e2e preflight timed out");
            return;
        }

        _ = stdout.Result;
        var reason = stderr.Result.Trim();
        if (process.ExitCode == 77)
        {
            SkipAfterCleanup(reason.Length > 0
                ? reason
                : "concurrent-push e2e prerequisites are unavailable");
        }
    }

    private void SkipAfterCleanup(string reason)
    {
        if (!RemovePulledRegistryImage())
        {
            throw new InvalidOperationException(
                $"could not remove registry:2 pulled by the preflight while skipping: {reason}");
        }
        Skip = reason;
    }

    private static Process? StartPreflight(ProcessStartInfo start)
    {
        try
        {
            return Process.Start(start);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
