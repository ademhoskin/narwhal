using System.Net;
using System.Net.Sockets;
using System.Text;
using Narwhal.Cli;
using Narwhal.Cloud;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

[Collection(MultiPlatformIntegrationTests.ProcessCollection.Name)]
public class StatusCommandIntegrationTests
{
    private const string Repository = "shop/state/aws-us-east-1-amd64-1a2b3c4d/status";

    [Fact]
    public async Task FetchesVerifiesAndPrintsSignedStatusAsJson()
    {
        var artifact = StatusTestData.Artifact();
        using var server = new RegistryServer(Repository, artifact);

        var (exitCode, output) = await RunAsync(server, json: true);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"schema\":\"narwhal.status/v1\"", output, StringComparison.Ordinal);
        Assert.Contains("\"phase\":\"idle\"", output, StringComparison.Ordinal);
        Assert.Contains(StatusTestData.HostId, output, StringComparison.Ordinal);
        Assert.Contains("\"last_error\":null", output, StringComparison.Ordinal);
        Assert.Contains("\"status_key_id\":\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DegradedStatusExitsThree()
    {
        var artifact = StatusTestData.Artifact(document: StatusTestData.Document(phase: ReconcilePhases.Degraded, withError: true));
        using var server = new RegistryServer(Repository, artifact);

        var (exitCode, output) = await RunAsync(server, json: false);

        Assert.Equal(3, exitCode);
        Assert.Contains("degraded", output, StringComparison.Ordinal);
        Assert.Contains("health_timeout", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedStatusExitsFive()
    {
        var artifact = StatusTestData.Artifact(document: StatusTestData.Document(phase: ReconcilePhases.Failed, withError: true));
        using var server = new RegistryServer(Repository, artifact);

        var (exitCode, output) = await RunAsync(server, json: true);

        Assert.Equal(5, exitCode);
        Assert.Contains("\"phase\":\"failed\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATamperedSignatureIsAReconciliationFailure()
    {
        var artifact = StatusTestData.Artifact();
        using var server = new RegistryServer(Repository, artifact, tamperSignature: true);

        var error = await Assert.ThrowsAsync<ReconcileException>(() =>
            RunAsync(server, json: true));

        Assert.Equal(ReconcileExitCode.Verification, error.ExitCode);
        Assert.Equal(CloudArtifactErrorCodes.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task RejectsAMismatchedTargetName()
    {
        var artifact = StatusTestData.Artifact();
        using var server = new RegistryServer(Repository, artifact);

        var error = await Assert.ThrowsAsync<ReconcileException>(() =>
            RunAsync(server, json: true, target: "staging"));

        Assert.Equal(ReconcileExitCode.Verification, error.ExitCode);
        Assert.Equal("target_mismatch", error.Code);
    }

    [Fact]
    public async Task AcceptsTheDerivedTargetId()
    {
        var artifact = StatusTestData.Artifact();
        using var server = new RegistryServer(Repository, artifact);

        var (exitCode, _) = await RunAsync(server, json: true, target: TargetId());

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RegistryOutageExitsThreeAtTheCli()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var configPath = WriteConfig(RegistryServer.FreePort(), directory);
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var output = new StringWriter();
        var errors = new StringWriter();
        Console.SetOut(output);
        Console.SetError(errors);
        try
        {
            var exitCode = await App.Main(["status", "--config", configPath, "--target", TargetId()]);

            Assert.Equal(3, exitCode);
            Assert.Contains("cannot read status", errors.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task MalformedTokenShapeExitsThreeAtTheCli()
    {
        var artifact = StatusTestData.Artifact();
        using var server = new RegistryServer(
            Repository,
            artifact,
            challengeToken: true,
            tokenBody: """{"token":123}""");
        var directory = ReconcileTestData.CreateStateDirectory();
        var configPath = WriteConfig(server.Port, directory, withPassword: true);
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var output = new StringWriter();
        var errors = new StringWriter();
        Console.SetOut(output);
        Console.SetError(errors);
        try
        {
            var exitCode = await App.Main(["status", "--config", configPath, "--target", TargetId()]);

            Assert.Equal(3, exitCode);
            Assert.Contains("cannot read status", errors.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        RegistryServer server,
        bool json,
        string? target = null)
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var configPath = WriteConfig(server.Port, directory);

        var output = new StringWriter();
        var reporter = new Reporter(json, false, false, output, new StringWriter());
        var options = CommandLine.Parse(
            ["status", "--config", configPath, "--target", target ?? TargetId(), "--json"]);
        var exitCode = await StatusCommand.RunAsync(options, reporter, CancellationToken.None);
        return (exitCode, output.ToString());
    }

    private static string TargetId() => StatusRepository.TargetId(new TargetConfig
    {
        Provider = "aws",
        Region = "us-east-1",
        Arch = "amd64",
        Selector = new Dictionary<string, string>(StringComparer.Ordinal) { ["stack"] = "prod" },
    });

    private static string WriteConfig(int port, string directory, bool withPassword = false)
    {
        var configPath = Path.Combine(directory, "agent.toml");
        var auth = withPassword
            ? """
              username = "agent"
              password = "password"
              """
            : "";
        File.WriteAllText(configPath, """
            project = "shop"
            image_registry = "__REGISTRY__"
            state_registry = "__REGISTRY__"
            desired_repo = "shop/state/aws-us-east-1-amd64-1a2b3c4d/desired"
            status_repo = "shop/state/aws-us-east-1-amd64-1a2b3c4d/status"
            state_dir = "__STATE__"
            status_public_keys = ["__PUBKEY__"]

            [target]
            provider = "aws"
            region = "us-east-1"
            arch = "amd64"
            selector = { stack = "prod" }

            [registry_auth]
            insecure = true
            __AUTH__
            """
            .Replace("__REGISTRY__", $"127.0.0.1:{port}", StringComparison.Ordinal)
            .Replace("__STATE__", directory.Replace("\\", "/", StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace("__AUTH__", auth, StringComparison.Ordinal)
            .Replace("__PUBKEY__", Convert.ToBase64String(CloudArtifactTestData.PublicKey1), StringComparison.Ordinal));
        if (withPassword && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        return configPath;
    }

    /// <summary>Loopback OCI registry serving one status manifest and its blobs.</summary>
    private sealed class RegistryServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        private readonly bool _challengeToken;
        private readonly string? _tokenBody;

        public RegistryServer(
            string repository,
            TestStatusArtifact artifact,
            bool tamperSignature = false,
            bool challengeToken = false,
            string? tokenBody = null)
        {
            _challengeToken = challengeToken;
            _tokenBody = tokenBody;
            Port = FreePort();
            Repository = repository;
            Payload = artifact.Payload;
            Signature = tamperSignature ? Tamper(artifact.Signature) : artifact.Signature;
            Manifest = tamperSignature
                ? StatusManifest.Build(
                    CloudEncoding.Sha256Digest(Payload),
                    Payload.Length,
                    CloudEncoding.Sha256Digest(Signature),
                    Signature.Length,
                    artifact.Document.StatusSeq,
                    fallback: false)
                : artifact.Manifest;

            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public int Port { get; }

        public string Repository { get; }

        public byte[] Payload { get; }

        public byte[] Signature { get; }

        public byte[] Manifest { get; }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                try
                {
                    Respond(context);
                }
                catch (Exception e) when (e is IOException or HttpListenerException)
                {
                }
            }
        }

        private void Respond(HttpListenerContext context)
        {
            var path = context.Request.Url!.AbsolutePath;
            if (path == "/token")
            {
                if (_tokenBody is null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                WriteText(context, _tokenBody, "application/json");
                return;
            }

            if (_challengeToken &&
                !(context.Request.Headers["Authorization"] ?? "")
                    .StartsWith("Bearer ", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 401;
                context.Response.Headers["WWW-Authenticate"] =
                    $"Bearer realm=\"http://127.0.0.1:{Port}/token\",service=\"registry.test\"";
                context.Response.Close();
                return;
            }

            var manifestPath = $"/v2/{Repository}/manifests/latest";
            if (path == manifestPath)
            {
                context.Response.Headers["Docker-Content-Digest"] = CloudEncoding.Sha256Digest(Manifest);
                Write(context, Manifest, StatusManifest.ManifestMediaType);
                return;
            }

            var prefix = $"/v2/{Repository}/blobs/";
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                var digest = path[prefix.Length..];
                if (digest == CloudEncoding.Sha256Digest(Payload))
                {
                    Write(context, Payload, "application/octet-stream");
                    return;
                }

                if (digest == CloudEncoding.Sha256Digest(Signature))
                {
                    Write(context, Signature, "application/octet-stream");
                    return;
                }
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }

        private static void Write(HttpListenerContext context, byte[] bytes, string contentType)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes);
            context.Response.Close();
        }

        private static void WriteText(HttpListenerContext context, string text, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            context.Response.StatusCode = 200;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes);
            context.Response.Close();
        }

        private static byte[] Tamper(byte[] signature)
        {
            var text = Encoding.UTF8.GetString(signature);
            var marker = "\"sig\":\"";
            var index = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var mutated = text[..index] + (text[index] == 'A' ? 'B' : 'A') + text[(index + 1)..];
            return CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(mutated));
        }

        public static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
        }
    }
}
