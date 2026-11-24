using System.Security.Cryptography;
using System.Text;
using Narwhal.Cloud;

namespace Narwhal.Tests;

internal static class CloudArtifactTestData
{
    public const string ImageDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public const string Key1SeedHex = "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60";
    public const string Key2SeedHex = "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb";

    public static byte[] Seed1 => Convert.FromHexString(Key1SeedHex);
    public static byte[] Seed2 => Convert.FromHexString(Key2SeedHex);

    public static byte[] PublicKey1 => Ed25519.PublicKeyFromSeed(Seed1);
    public static byte[] PublicKey2 => Ed25519.PublicKeyFromSeed(Seed2);

    public static string KeyId1 => KeyId.Derive(PublicKey1);
    public static string KeyId2 => KeyId.Derive(PublicKey2);

    public static string PublicKey1Base64 => Convert.ToBase64String(PublicKey1);

    public static DateTimeOffset Now { get; } = new(2026, 11, 24, 18, 30, 0, TimeSpan.Zero);

    public static byte[] Canonicalize(string json) =>
        CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(json));

    public static string DefaultTrustEntry(byte[] seed, DateTimeOffset? notAfter = null) =>
        TrustEntry(Ed25519.PublicKeyFromSeed(seed), notAfter);

    public static string TrustEntry(byte[] publicKey, DateTimeOffset? notAfter = null)
    {
        var keyId = KeyId.Derive(publicKey);
        var encoded = Convert.ToBase64String(publicKey);
        var suffix = notAfter is null
            ? ""
            : $",\"not_after\":\"{notAfter.Value:yyyy-MM-dd'T'HH:mm:ss'Z'}\"";
        return $"{{\"key_id\":\"{keyId}\",\"public_key\":\"{encoded}\"{suffix}}}";
    }

    public static string PayloadJson(
        long generation = 42,
        string project = "shop",
        string provider = "aws",
        string region = "us-east-1",
        string arch = "amd64",
        string? selector = null,
        string? ingress = null,
        string? trust = null,
        bool includeTrust = true,
        string? previous = null,
        string? images = null,
        string? services = null,
        string? deploy = null,
        string? agent = null)
    {
        selector ??= """{"stack":"prod"}""";
        ingress ??= """[{"protocol":"tcp","port":8080,"source":["203.0.113.0/24"]}]""";
        trust ??= $"[{DefaultTrustEntry(Seed1)}]";
        images ??= "{\"" + ImageDigest + "\":{\"repository\":\"registry.example.com/shop/api\",\"platform\":\"linux/" + arch + "\",\"size\":48231011}}";
        services ??= "{\"api\":{\"image\":\"" + ImageDigest + "\",\"ports\":[\"8080:8080\"]}}";

        var builder = new StringBuilder();
        builder.Append('{');
        builder.Append($"\"schema\":\"narwhal.desired/v1\",\"version\":1,\"project\":\"{project}\",\"generation\":{generation},");
        builder.Append("\"created\":\"2026-11-24T18:30:00Z\",");
        if (previous is not null)
            builder.Append($"\"previous\":{previous},");
        builder.Append($"\"target\":{{\"provider\":\"{provider}\",\"region\":\"{region}\",\"arch\":\"{arch}\",\"selector\":{selector},\"ingress\":{ingress}}},");
        if (includeTrust)
            builder.Append($"\"trust\":{trust},");
        builder.Append($"\"images\":{images},");
        builder.Append($"\"services\":{services}");
        if (deploy is not null)
            builder.Append($",\"deploy\":{deploy}");
        if (agent is not null)
            builder.Append($",\"agent\":{agent}");
        builder.Append('}');
        return builder.ToString();
    }

    public static byte[] Envelope(byte[] payload, params byte[][] seeds)
    {
        if (seeds.Length == 0)
            seeds = [Seed1];

        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(payload));
        var signatures = seeds.Select(seed =>
        {
            var signature = SignPayload(seed, payload);
            return $"{{\"alg\":\"ed25519\",\"key_id\":\"{KeyId.Derive(Ed25519.PublicKeyFromSeed(seed))}\",\"sig\":\"{CloudEncoding.ToBase64Url(signature)}\"}}";
        });

        var json = $"{{\"schema\":\"narwhal.signature/v1\",\"payload_digest\":\"{digest}\",\"signatures\":[{string.Join(',', signatures)}]}}";
        return Encoding.UTF8.GetBytes(json);
    }

    public static byte[] SignPayload(byte[] seed, byte[] payload)
    {
        var message = new byte[DesiredArtifactVerifier.DomainSeparator.Length + payload.Length];
        Encoding.ASCII.GetBytes(DesiredArtifactVerifier.DomainSeparator, message);
        payload.CopyTo(message.AsSpan(DesiredArtifactVerifier.DomainSeparator.Length));
        return Ed25519.Sign(seed, message);
    }

    public static string TrustJson(params string[] entries) => $"[{string.Join(',', entries)}]";

    public static TrustSet Trust(params byte[][] seeds) =>
        TrustSet.FromKeys(seeds.Select(seed =>
        {
            var publicKey = Ed25519.PublicKeyFromSeed(seed);
            return new TrustedKey { KeyId = KeyId.Derive(publicKey), PublicKey = publicKey };
        }));

    public static TargetExpectation Target(
        string project = "shop",
        string provider = "aws",
        string region = "us-east-1",
        string arch = "amd64",
        IReadOnlyDictionary<string, string>? selector = null) =>
        new()
        {
            Project = project,
            Provider = provider,
            Region = region,
            Arch = arch,
            Selector = selector ?? new Dictionary<string, string> { ["stack"] = "prod" },
        };

    public static ArtifactVerificationContext Context(
        byte[] payload,
        byte[] envelope,
        TrustSet? trust = null,
        TargetExpectation? target = null,
        GenerationState? generation = null) =>
        RawContext(
            [
                new ArtifactLayer(DesiredArtifactLayout.PayloadMediaType, CloudEncoding.Sha256Digest(payload), payload.Length),
                new ArtifactLayer(DesiredArtifactLayout.SignatureMediaType, CloudEncoding.Sha256Digest(envelope), envelope.Length),
            ],
            trust,
            target,
            generation);

    public static ArtifactVerificationContext RawContext(
        IReadOnlyList<ArtifactLayer> layers,
        TrustSet? trust = null,
        TargetExpectation? target = null,
        GenerationState? generation = null) =>
        new()
        {
            Layers = layers,
            TrustSet = trust ?? Trust(Seed1),
            Target = target ?? Target(),
            Now = Now,
            Generation = generation ?? new GenerationState(),
        };

}
