using System.Diagnostics;
using System.Text;
using Narwhal.Cli;
using Narwhal.Cloud;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

[CollectionDefinition("oras-subprocess", DisableParallelization = true)]
public sealed class OrasSubprocessCollection
{
}

/// <summary>Exercises the oras-backed registry client against a fake executable.</summary>
[Collection("oras-subprocess")]
public class OciRegistryClientTests
{
    private const string Repository = "shop/state/target/desired";

    [Fact]
    public async Task FetchesAManifestWithAnExplicitArgumentList()
    {
        var artifact = ReconcileTestData.Artifact(CloudArtifactTestData.PayloadJson());
        using var oras = new FakeOras();
        oras.Output(artifact.Manifest.Bytes);
        oras.Descriptor(artifact.Manifest.Digest, artifact.Manifest.Bytes.Length);

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        var manifest = await client.GetManifestAsync(Repository, "desired", CancellationToken.None);

        Assert.Equal(artifact.Manifest.Digest, manifest.Digest);
        Assert.Equal(artifact.Manifest.Bytes, manifest.Bytes);
        Assert.Equal(artifact.Manifest.Selection, manifest.Selection);

        var args = oras.Arguments;
        Assert.Equal(9, args.Length);
        Assert.Equal(["manifest", "fetch", "--output"], args[..3]);
        Assert.EndsWith(".json", args[3], StringComparison.Ordinal);
        Assert.StartsWith("manifest-", Path.GetFileName(args[3]), StringComparison.Ordinal);
        Assert.Equal("--descriptor", args[4]);
        Assert.Equal("--plain-http", args[5]);
        Assert.Equal("--registry-config", args[6]);
        Assert.Equal("registry.test/" + Repository + ":desired", args[8]);
    }

    [Fact]
    public async Task FetchesAManifestByDigestReference()
    {
        var artifact = ReconcileTestData.Artifact(CloudArtifactTestData.PayloadJson());
        using var oras = new FakeOras();
        oras.Output(artifact.Manifest.Bytes);
        oras.Descriptor(artifact.Manifest.Digest, artifact.Manifest.Bytes.Length);

        using var client = Client(new RegistryAuthConfig());
        var manifest = await client.GetManifestAsync(Repository, artifact.Manifest.Digest, CancellationToken.None);

        Assert.Equal(artifact.Manifest.Digest, manifest.Digest);
        Assert.Equal(
            "registry.test/" + Repository + "@" + artifact.Manifest.Digest,
            oras.Arguments[^1]);
        Assert.DoesNotContain("--plain-http", oras.Arguments);
    }

    [Fact]
    public async Task FetchesAStatusManifestWithTheStatusLayout()
    {
        var payload = "{\"status\":true}"u8.ToArray();
        var signature = "signature"u8.ToArray();
        var manifest = StatusManifest.Build(
            CloudEncoding.Sha256Digest(payload),
            payload.Length,
            CloudEncoding.Sha256Digest(signature),
            signature.Length,
            7,
            fallback: false);

        using var oras = new FakeOras();
        oras.Output(manifest);
        oras.Descriptor(CloudEncoding.Sha256Digest(manifest), manifest.Length);

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        var fetched = await client.GetStatusManifestAsync("shop/state/target/status", "latest", CancellationToken.None);

        Assert.Equal(CloudEncoding.Sha256Digest(manifest), fetched.Digest);
        Assert.Equal(StatusArtifactLayout.PayloadMediaType, fetched.Selection.Payload.MediaType);
        Assert.Equal(StatusArtifactLayout.SignatureMediaType, fetched.Selection.Signature.MediaType);
    }

    [Fact]
    public async Task RejectsADescriptorDigestThatDoesNotMatchTheManifestBytes()
    {
        var artifact = ReconcileTestData.Artifact(CloudArtifactTestData.PayloadJson());
        using var oras = new FakeOras();
        oras.Output(artifact.Manifest.Bytes);
        oras.Descriptor(Digest('f'), artifact.Manifest.Bytes.Length);

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetManifestAsync(Repository, "desired", CancellationToken.None));
        Assert.Contains("reported by the registry", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsPinnedManifestBytesThatDoNotMatchTheDigest()
    {
        var artifact = ReconcileTestData.Artifact(CloudArtifactTestData.PayloadJson());
        using var oras = new FakeOras();
        oras.Output(artifact.Manifest.Bytes);
        oras.Descriptor(artifact.Manifest.Digest, artifact.Manifest.Bytes.Length);

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetManifestAsync(Repository, Digest('a'), CancellationToken.None));
        Assert.Contains("pinned digest", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchesAVerifiedBlobWithAnExplicitArgumentList()
    {
        var body = "blob payload"u8.ToArray();
        var digest = CloudEncoding.Sha256Digest(body);
        using var oras = new FakeOras();
        oras.Output(body);

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        var fetched = await client.GetBlobAsync(Repository, digest, CancellationToken.None);

        Assert.Equal(body, fetched);
        var args = oras.Arguments;
        Assert.Equal(9, args.Length);
        Assert.Equal(["blob", "fetch", "--no-tty", "--output"], args[..4]);
        Assert.EndsWith(".bin", args[4], StringComparison.Ordinal);
        Assert.Equal("--plain-http", args[5]);
        Assert.Equal("--registry-config", args[6]);
        Assert.Equal("registry.test/" + Repository + "@" + digest, args[8]);
    }

    [Fact]
    public async Task RejectsABlobWhoseBytesDoNotMatchItsDigest()
    {
        using var oras = new FakeOras();
        oras.Output("tampered"u8.ToArray());

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<CloudArtifactException>(
            () => client.GetBlobAsync(Repository, Digest('a'), CancellationToken.None));
        Assert.Equal(CloudArtifactErrorCodes.DigestMismatch, error.Code);
    }

    [Fact]
    public async Task RejectsABlobAboveTheSizeLimit()
    {
        using var oras = new FakeOras();
        oras.Truncate(OciRegistryClient.MaximumBlobBytes + 1);

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetBlobAsync(Repository, Digest('a'), CancellationToken.None));
        Assert.Contains("limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAMissingOutputFileAsATransportFailure()
    {
        using var oras = new FakeOras();

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetBlobAsync(Repository, Digest('a'), CancellationToken.None));
        Assert.Contains("did not write", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapsAMissingManifestToStatusCode404()
    {
        using var oras = new FakeOras();
        oras.Fail(1, "Error response from registry: failed to resolve reference: not found");

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetManifestAsync(Repository, "desired", CancellationToken.None));

        Assert.Equal(404, error.StatusCode);
        Assert.False(error.Auth);
    }

    [Fact]
    public async Task MapsAnUnauthorizedResponseToAnAuthFailure()
    {
        using var oras = new FakeOras();
        oras.Fail(1, "Error response from registry: unauthorized: authentication required");

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetManifestAsync(Repository, "desired", CancellationToken.None));

        Assert.True(error.Auth);
        Assert.Equal(401, error.StatusCode);
    }

    [Fact]
    public async Task ParsesAnExplicitStatusCodeFromStderr()
    {
        using var oras = new FakeOras();
        oras.Fail(1, "GET \"https://registry.test/v2/manifests/desired\" unexpected response status code 503");

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetManifestAsync(Repository, "desired", CancellationToken.None));

        Assert.Equal(503, error.StatusCode);
        Assert.False(error.Auth);
    }

    [Fact]
    public async Task CapsCapturedStderr()
    {
        using var oras = new FakeOras();
        oras.Fail(1, new string('x', 100_000));

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetManifestAsync(Repository, "desired", CancellationToken.None));

        Assert.Contains("output truncated", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length < 9_000, $"message was {error.Message.Length} characters");
    }

    [Fact]
    public async Task EnforcesACommandTimeout()
    {
        using var oras = new FakeOras();
        oras.Sleep(30);
        oras.Output("late"u8.ToArray());

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Insecure = true },
            reporter: null,
            verbose: false,
            commandTimeout: TimeSpan.FromMilliseconds(300));

        var started = DateTime.UtcNow;
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetBlobAsync(Repository, Digest('a'), CancellationToken.None));
        Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SendsThePasswordOnStdinAndNotInArgv()
    {
        var body = "blob payload"u8.ToArray();
        var digest = CloudEncoding.Sha256Digest(body);
        using var oras = new FakeOras();
        oras.Output(body);
        oras.CaptureStdin();

        var errors = new StringWriter();
        var reporter = new Reporter(json: false, verbose: true, color: false, stdout: TextWriter.Null, stderr: errors);
        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Insecure = true, Username = "agent", Password = "s3cret" },
            reporter);

        await client.GetBlobAsync(Repository, digest, CancellationToken.None);

        var args = oras.Arguments;
        Assert.Contains("-u", args);
        Assert.Contains("agent", args);
        Assert.Contains("--password-stdin", args);
        Assert.DoesNotContain("s3cret", args);
        Assert.Contains("--registry-config", args);
        Assert.Equal("s3cret", oras.Stdin);
        Assert.DoesNotContain("s3cret", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal("{}", oras.AuthFile);
    }

    [Fact]
    public async Task GeneratesARegistryTokenAuthFile()
    {
        var body = "blob payload"u8.ToArray();
        var digest = CloudEncoding.Sha256Digest(body);
        using var oras = new FakeOras();
        oras.Output(body);
        oras.CaptureAuthFile();

        using var client = Client(new RegistryAuthConfig { Insecure = true, Token = "registry-token" });
        await client.GetBlobAsync(Repository, digest, CancellationToken.None);

        var args = oras.Arguments;
        Assert.Contains("--registry-config", args);
        Assert.DoesNotContain("registry-token", args);
        Assert.DoesNotContain("--password-stdin", args);

        var auth = oras.AuthFile;
        Assert.Contains("\"auths\"", auth, StringComparison.Ordinal);
        Assert.Contains("\"registry.test\":{\"registrytoken\":\"registry-token\"}", auth, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratesACredentialHelperAuthFile()
    {
        var body = "blob payload"u8.ToArray();
        var digest = CloudEncoding.Sha256Digest(body);
        using var oras = new FakeOras();
        oras.Output(body);
        oras.CaptureAuthFile();

        using var client = Client(new RegistryAuthConfig { Insecure = true, CredentialHelper = "pass" });
        await client.GetBlobAsync(Repository, digest, CancellationToken.None);

        Assert.Contains("--registry-config", oras.Arguments);
        Assert.Contains(
            "\"credHelpers\":{\"registry.test\":\"pass\"}",
            oras.AuthFile,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindsACredentialHelperByNameOnPath()
    {
        var body = "blob payload"u8.ToArray();
        var digest = CloudEncoding.Sha256Digest(body);
        using var oras = new FakeOras();
        oras.Output(body);
        oras.CaptureAuthFile();

        using var client = Client(new RegistryAuthConfig { Insecure = true, CredentialHelper = "secretservice" });
        await client.GetBlobAsync(Repository, digest, CancellationToken.None);

        Assert.Contains("\"credHelpers\":{\"registry.test\":\"secretservice\"}", oras.AuthFile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptsAnAbsoluteCredentialHelperPath()
    {
        var body = "blob payload"u8.ToArray();
        var digest = CloudEncoding.Sha256Digest(body);
        using var oras = new FakeOras();
        oras.Output(body);
        oras.CaptureAuthFile();
        oras.LogEnvironment("PATH");

        var helper = CreateHelper(executable: true);

        var client = Client(new RegistryAuthConfig { Insecure = true, CredentialHelper = helper });
        try
        {
            await client.GetBlobAsync(Repository, digest, CancellationToken.None);

            Assert.Contains("\"credHelpers\":{\"registry.test\":\"narwhal\"}", oras.AuthFile, StringComparison.Ordinal);

            var path = oras.EnvironmentVariables["PATH"].Split(':', StringSplitOptions.RemoveEmptyEntries);
            var shim = Path.Combine(path[0], "docker-credential-narwhal");
            Assert.True(File.Exists(shim), $"expected a helper shim at {shim}");
            Assert.Equal(
                Path.GetFullPath(helper),
                File.ResolveLinkTarget(shim, returnFinalTarget: true)?.FullName);
        }
        finally
        {
            client.Dispose();
            File.Delete(helper);
        }
    }

    [Fact]
    public async Task ReportsAMissingAbsoluteCredentialHelperAsATypedFailure()
    {
        var digest = Digest('a');
        using var oras = new FakeOras();
        oras.Output([]);

        var missing = Path.Combine(Path.GetTempPath(), "narwhal-missing-helper-" + Guid.NewGuid().ToString("N"));
        using var client = Client(new RegistryAuthConfig { Insecure = true, CredentialHelper = missing });
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetBlobAsync(Repository, digest, CancellationToken.None));

        Assert.True(error.Auth);
        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsANonExecutableAbsoluteCredentialHelperAsATypedFailure()
    {
        using var oras = new FakeOras();
        oras.Output([]);

        var helper = CreateHelper(executable: false);
        try
        {
            using var client = Client(new RegistryAuthConfig { Insecure = true, CredentialHelper = helper });
            var error = await Assert.ThrowsAsync<RegistryException>(
                () => client.GetBlobAsync(Repository, Digest('a'), CancellationToken.None));

            Assert.True(error.Auth);
            Assert.Contains("not executable", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(helper);
        }
    }

    [Fact]
    public async Task BlobExistsUsesTheDescriptorAndTreatsAMissingBlobAsAbsent()
    {
        using var oras = new FakeOras();
        using var client = Client(new RegistryAuthConfig { Insecure = true });
        Assert.True(await client.BlobExistsAsync(Repository, Digest('a'), CancellationToken.None));

        oras.Fail(1, "Error response from registry: blob unknown: not found");
        Assert.False(await client.BlobExistsAsync(Repository, Digest('a'), CancellationToken.None));
        Assert.Equal(["blob", "fetch", "--descriptor", "--no-tty"], oras.Arguments[..4]);
    }

    [Fact]
    public async Task BlobExistsTreatsAnAuthFailureAsAbsent()
    {
        using var oras = new FakeOras();
        oras.Fail(1, "Error response from registry: unauthorized: authentication required");

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        Assert.False(await client.BlobExistsAsync(Repository, Digest('a'), CancellationToken.None));
    }

    [Fact]
    public async Task AlwaysPassesAPrivateEmptyRegistryConfigWhenNoAuthIsConfigured()
    {
        var body = "blob payload"u8.ToArray();
        var digest = CloudEncoding.Sha256Digest(body);
        using var oras = new FakeOras();
        oras.Output(body);
        oras.CaptureAuthFile();

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        await client.GetBlobAsync(Repository, digest, CancellationToken.None);

        Assert.Contains("--registry-config", oras.Arguments);
        Assert.Equal("{}", oras.AuthFile);
        Assert.True(File.Exists(oras.AuthFilePath));
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(oras.AuthFilePath);
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead |
                        UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite));
        }
    }

    [Fact]
    public async Task DoesNotUseAnAmbientDockerCredentialWhenNoAuthIsConfigured()
    {
        var ambient = Path.Combine(Path.GetTempPath(), "narwhal-ambient-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ambient);
        File.WriteAllText(
            Path.Combine(ambient, "config.json"),
            """{"auths":{"registry.test":{"auth":"YWdlbnQ6c2VjcmV0"}}}""");

        var original = Environment.GetEnvironmentVariable("DOCKER_CONFIG");
        Environment.SetEnvironmentVariable("DOCKER_CONFIG", ambient);
        try
        {
            var body = "blob payload"u8.ToArray();
            using var oras = new FakeOras();
            oras.Output(body);
            oras.CaptureAuthFile();

            using var client = Client(new RegistryAuthConfig { Insecure = true });
            await client.GetBlobAsync(Repository, CloudEncoding.Sha256Digest(body), CancellationToken.None);

            Assert.Equal("{}", oras.AuthFile);
            Assert.Equal("__NO_CONFIG__", oras.DockerConfig);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOCKER_CONFIG", original);
            Directory.Delete(ambient, recursive: true);
        }
    }

    [Fact]
    public async Task PlacesTheAuthFileUnderTheRuntimeDirectoryAndRemovesItOnDispose()
    {
        var runtime = Path.Combine(Path.GetTempPath(), "narwhal-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtime);
        var original = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtime);
        try
        {
            var body = "blob payload"u8.ToArray();
            using var oras = new FakeOras();
            oras.Output(body);

            var client = Client(new RegistryAuthConfig { Insecure = true });
            string authPath;
            try
            {
                await client.GetBlobAsync(Repository, CloudEncoding.Sha256Digest(body), CancellationToken.None);
                authPath = oras.AuthFilePath;
                Assert.StartsWith(Path.Combine(runtime, "narwhal"), authPath, StringComparison.Ordinal);
                Assert.True(File.Exists(authPath));
            }
            finally
            {
                client.Dispose();
            }

            Assert.False(File.Exists(authPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", original);
            Directory.Delete(runtime, recursive: true);
        }
    }

    [Fact]
    public async Task StripsAgentCredentialsFromTheOrasChild()
    {
        var names = new[]
        {
            "NARWHAL_REGISTRY_USERNAME",
            "NARWHAL_REGISTRY_PASSWORD",
            "NARWHAL_REGISTRY_TOKEN",
            "NARWHAL_REGISTRY_CREDENTIAL_HELPER",
            "NARWHAL_REGISTRY_INSECURE",
            "NARWHAL_IMAGE_REGISTRY_USERNAME",
            "NARWHAL_IMAGE_REGISTRY_PASSWORD",
            "NARWHAL_IMAGE_REGISTRY_TOKEN",
            "NARWHAL_IMAGE_REGISTRY_CREDENTIAL_HELPER",
            "NARWHAL_IMAGE_REGISTRY_INSECURE",
            "DOCKER_AUTH_CONFIG",
        };
        var originals = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        foreach (var name in names)
            Environment.SetEnvironmentVariable(name, "secret-" + name);

        try
        {
            var body = "blob payload"u8.ToArray();
            using var oras = new FakeOras();
            oras.Output(body);
            oras.LogEnvironment(names);

            using var client = Client(new RegistryAuthConfig { Insecure = true });
            await client.GetBlobAsync(Repository, CloudEncoding.Sha256Digest(body), CancellationToken.None);

            foreach (var name in names)
                Assert.Equal("__UNSET__", oras.EnvironmentVariables[name]);
        }
        finally
        {
            foreach (var (name, value) in originals)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task CapsCapturedOutputForANewlineFreeFlood()
    {
        using var oras = new FakeOras();
        oras.Fail(1, "");
        oras.EmitHuge(8 * 1024 * 1024);

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetManifestAsync(Repository, "desired", CancellationToken.None));

        Assert.Contains("output truncated", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length < 9_000, $"message was {error.Message.Length} characters");
    }

    [Fact]
    public async Task KillsTheWholeProcessTreeOnCallerCancellation()
    {
        using var oras = new FakeOras();
        oras.Sleep(30);
        oras.CapturePid();
        oras.SpawnChild();
        oras.Output([]);

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetBlobAsync(Repository, Digest('a'), cancellation.Token));

        var pid = int.Parse(File.ReadAllText(oras.PidPath).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var childPid = int.Parse(File.ReadAllText(oras.ChildPidPath).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(await WaitForExitAsync(pid), $"oras process {pid} survived cancellation");
        Assert.True(await WaitForExitAsync(childPid), $"oras child {childPid} survived cancellation");
    }

    [Theory]
    [InlineData("unexpected response status code 403", 403, true)]
    [InlineData("unexpected response status code 429", 429, false)]
    [InlineData("unexpected response status code 500", 500, false)]
    [InlineData("tls: failed to verify certificate: x509: certificate signed by unknown authority", null, false)]
    public async Task MapsRegistryFailureStatuses(string stderr, int? status, bool auth)
    {
        using var oras = new FakeOras();
        oras.Fail(1, stderr);

        using var client = Client(new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetBlobAsync(Repository, Digest('a'), CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(auth, error.Auth);
    }

    private static async Task<bool> WaitForExitAsync(int pid)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    private static string CreateHelper(bool executable)
    {
        var path = Path.Combine(Path.GetTempPath(), "narwhal-helper-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "#!/bin/sh\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                executable
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                      UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                      UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite |
                      UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return path;
    }

    private static OciRegistryClient Client(RegistryAuthConfig auth) =>
        new("registry.test", auth, reporter: null, verbose: false);

    private static string Digest(char value) => "sha256:" + new string(value, 64);
}

/// <summary>A fake <c>oras</c> executable driven by environment variables.</summary>
internal sealed class FakeOras : IDisposable
{
    private static readonly string[] ManagedVariables =
    [
        "NARWHAL_ORAS",
        "ORAS_FAKE_LOG",
        "ORAS_FAKE_FIRST_LOG",
        "ORAS_FAKE_STDIN",
        "ORAS_FAKE_AUTH_COPY",
        "ORAS_FAKE_AUTH_PATH",
        "ORAS_FAKE_DOCKER_CONFIG_COPY",
        "ORAS_FAKE_ENV_LOG",
        "ORAS_FAKE_ENV_VARS",
        "ORAS_FAKE_FILE",
        "ORAS_FAKE_TRUNCATE",
        "ORAS_FAKE_STDOUT",
        "ORAS_FAKE_STDERR",
        "ORAS_FAKE_CAPTURE",
        "ORAS_FAKE_SLEEP",
        "ORAS_FAKE_PID",
        "ORAS_FAKE_CHILD_PID",
        "ORAS_FAKE_HUGE",
        "ORAS_FAKE_EXIT",
    ];

    private readonly string _directory;
    private readonly Dictionary<string, string?> _originals = new(StringComparer.Ordinal);
    private readonly string _logPath;
    private readonly string _firstLogPath;
    private readonly string _stdinPath;
    private readonly string _authCopyPath;
    private readonly string _authPathPath;
    private readonly string _dockerConfigCopyPath;
    private readonly string _environmentPath;

    public FakeOras()
    {
        _directory = Path.Combine(Path.GetTempPath(), "narwhal-fake-oras-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _logPath = Path.Combine(_directory, "argv.log");
        _firstLogPath = Path.Combine(_directory, "argv-first.log");
        _stdinPath = Path.Combine(_directory, "stdin");
        _authCopyPath = Path.Combine(_directory, "auth-copy.json");
        _authPathPath = Path.Combine(_directory, "auth-path");
        _dockerConfigCopyPath = Path.Combine(_directory, "docker-config-copy.json");
        _environmentPath = Path.Combine(_directory, "environment.log");

        File.WriteAllText(_logPath, "");
        File.WriteAllText(_firstLogPath, "");
        foreach (var variable in ManagedVariables)
            _originals[variable] = Environment.GetEnvironmentVariable(variable);

        var script = Path.Combine(_directory, "oras");
        File.WriteAllText(script, Script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Set("NARWHAL_ORAS", script);
        Set("ORAS_FAKE_LOG", _logPath);
        Set("ORAS_FAKE_FIRST_LOG", _firstLogPath);
        Set("ORAS_FAKE_AUTH_COPY", _authCopyPath);
        Set("ORAS_FAKE_AUTH_PATH", _authPathPath);
        Set("ORAS_FAKE_DOCKER_CONFIG_COPY", _dockerConfigCopyPath);
        Set("ORAS_FAKE_ENV_LOG", _environmentPath);
    }

    public string[] Arguments =>
        File.Exists(_logPath)
            ? File.ReadAllLines(_logPath)
            : [];

    public string[] FirstArguments =>
        File.Exists(_firstLogPath)
            ? File.ReadAllLines(_firstLogPath)
            : [];

    public string Stdin => File.Exists(_stdinPath) ? File.ReadAllText(_stdinPath) : "";

    public string AuthFile => File.Exists(_authCopyPath) ? File.ReadAllText(_authCopyPath) : "";

    public string AuthFilePath => File.Exists(_authPathPath) ? File.ReadAllText(_authPathPath).Trim() : "";

    public string DockerConfig => File.Exists(_dockerConfigCopyPath) ? File.ReadAllText(_dockerConfigCopyPath).Trim() : "";

    public IReadOnlyDictionary<string, string> EnvironmentVariables =>
        File.Exists(_environmentPath)
            ? File.ReadAllLines(_environmentPath)
                .Where(line => line.Contains('='))
                .Select(line => (line[..line.IndexOf('=')], line[(line.IndexOf('=') + 1)..]))
                .ToDictionary(pair => pair.Item1, pair => pair.Item2, StringComparer.Ordinal)
            : new Dictionary<string, string>();

    public string CapturePath { get; } = Path.Combine(Path.GetTempPath(), "narwhal-fake-capture-" + Guid.NewGuid().ToString("N"));

    public string PidPath => Path.Combine(_directory, "pid");

    public string ChildPidPath => Path.Combine(_directory, "child-pid");

    public void Output(byte[] bytes)
    {
        var path = Path.Combine(_directory, "output");
        File.WriteAllBytes(path, bytes);
        Set("ORAS_FAKE_FILE", path);
    }

    public void Descriptor(string digest, long size) =>
        Stdout($$"""{"mediaType":"application/vnd.oci.image.manifest.v1+json","digest":"{{digest}}","size":{{size}}}""");

    public void Stdout(string text)
    {
        var path = Path.Combine(_directory, "stdout");
        File.WriteAllText(path, text);
        Set("ORAS_FAKE_STDOUT", path);
    }

    public void Fail(int exitCode, string stderr)
    {
        var path = Path.Combine(_directory, "stderr");
        File.WriteAllText(path, stderr);
        Set("ORAS_FAKE_STDERR", path);
        Set("ORAS_FAKE_EXIT", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Truncate(long size) =>
        Set("ORAS_FAKE_TRUNCATE", size.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void CaptureStdin() => Set("ORAS_FAKE_STDIN", _stdinPath);

    public void CaptureAuthFile() => Set("ORAS_FAKE_AUTH_COPY", _authCopyPath);

    public void LogEnvironment(params string[] names) =>
        Set("ORAS_FAKE_ENV_VARS", string.Join(' ', names));

    public void CapturePid() => Set("ORAS_FAKE_PID", PidPath);

    public void SpawnChild() => Set("ORAS_FAKE_CHILD_PID", ChildPidPath);

    public void EmitHuge(long bytes) =>
        Set("ORAS_FAKE_HUGE", bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void CaptureLastArgument() => Set("ORAS_FAKE_CAPTURE", CapturePath);

    public void Sleep(int seconds) =>
        Set("ORAS_FAKE_SLEEP", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void Dispose()
    {
        foreach (var (variable, value) in _originals)
            Environment.SetEnvironmentVariable(variable, value);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    private void Set(string variable, string value) => Environment.SetEnvironmentVariable(variable, value);

    private const string Script = """
        #!/usr/bin/env bash
        set -u
        log="${ORAS_FAKE_LOG:?}"
        : > "$log"
        if [ -n "${ORAS_FAKE_FIRST_LOG:-}" ] && [ ! -s "$ORAS_FAKE_FIRST_LOG" ]; then
          for a in "$@"; do printf '%s\n' "$a" >> "$ORAS_FAKE_FIRST_LOG"; done
        fi
        if [ -n "${ORAS_FAKE_ENV_LOG:-}" ]; then
          : > "$ORAS_FAKE_ENV_LOG"
          for name in ${ORAS_FAKE_ENV_VARS:-}; do
            eval "value=\${$name-__UNSET__}"
            printf '%s=%s\n' "$name" "$value" >> "$ORAS_FAKE_ENV_LOG"
          done
        fi
        if [ -n "${ORAS_FAKE_PID:-}" ]; then echo $$ > "$ORAS_FAKE_PID"; fi
        if [ -n "${ORAS_FAKE_CHILD_PID:-}" ]; then
          sleep 30 &
          echo $! > "$ORAS_FAKE_CHILD_PID"
        fi
        prev=""
        last=""
        for a in "$@"; do
          printf '%s\n' "$a" >> "$log"
          if [ "$prev" = "--registry-config" ] && [ -n "${ORAS_FAKE_AUTH_COPY:-}" ]; then
            cp "$a" "$ORAS_FAKE_AUTH_COPY" 2>/dev/null || true
            printf '%s\n' "$a" > "$ORAS_FAKE_AUTH_PATH"
          fi
          prev="$a"
          last="$a"
        done
        if [ -n "${ORAS_FAKE_DOCKER_CONFIG_COPY:-}" ]; then
          if [ -n "${DOCKER_CONFIG:-}" ] && [ -f "$DOCKER_CONFIG/config.json" ]; then
            cp "$DOCKER_CONFIG/config.json" "$ORAS_FAKE_DOCKER_CONFIG_COPY" 2>/dev/null || true
          else
            printf '%s\n' "__NO_CONFIG__" > "$ORAS_FAKE_DOCKER_CONFIG_COPY"
          fi
        fi
        if [ -n "${ORAS_FAKE_STDIN:-}" ]; then
          cat > "$ORAS_FAKE_STDIN"
        fi
        out=""
        prev=""
        for a in "$@"; do
          if [ "$prev" = "--output" ]; then out="$a"; fi
          prev="$a"
        done
        if [ -n "$out" ]; then
          if [ -n "${ORAS_FAKE_TRUNCATE:-}" ]; then
            truncate -s "$ORAS_FAKE_TRUNCATE" "$out"
          elif [ -n "${ORAS_FAKE_FILE:-}" ]; then
            cp "$ORAS_FAKE_FILE" "$out"
          fi
        fi
        if [ -n "${ORAS_FAKE_CAPTURE:-}" ] && [ -n "$last" ]; then
          cp "$last" "$ORAS_FAKE_CAPTURE" 2>/dev/null || true
        fi
        if [ -n "${ORAS_FAKE_STDOUT:-}" ]; then cat "$ORAS_FAKE_STDOUT"; fi
        if [ -n "${ORAS_FAKE_STDERR:-}" ]; then cat "$ORAS_FAKE_STDERR" >&2; fi
        if [ -n "${ORAS_FAKE_HUGE:-}" ]; then head -c "$ORAS_FAKE_HUGE" /dev/zero | tr '\0' 'x'; fi
        if [ -n "${ORAS_FAKE_SLEEP:-}" ]; then sleep "$ORAS_FAKE_SLEEP"; fi
        exit "${ORAS_FAKE_EXIT:-0}"
        """;
}
