using System.Text;
using Narwhal.Cloud;
using Xunit;

namespace Narwhal.Tests;

public class CloudArtifactTrustTests
{
    [Fact]
    public void DerivesKeyIdFromFirstEightHashBytes()
    {
        Assert.Equal("21fe31dfa154a261", KeyId.Derive(CloudArtifactTestData.PublicKey1));
        Assert.Equal("39f713d0a644253f", KeyId.Derive(CloudArtifactTestData.PublicKey2));
        Assert.Equal(16, KeyId.Derive(CloudArtifactTestData.PublicKey1).Length);
    }

    [Fact]
    public void DeriveRejectsWrongKeyLength()
    {
        Assert.Throws<ArgumentException>(() => KeyId.Derive(new byte[31]));
    }

    [Fact]
    public void ParsesAValidTrustSet()
    {
        var json = CloudArtifactTestData.TrustJson(
            CloudArtifactTestData.DefaultTrustEntry(CloudArtifactTestData.Seed1),
            CloudArtifactTestData.DefaultTrustEntry(CloudArtifactTestData.Seed2));

        var trust = TrustSet.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(2, trust.Keys.Count);
        Assert.True(trust.TryFindUnexpired(CloudArtifactTestData.KeyId1, CloudArtifactTestData.Now, out var first));
        Assert.Equal(CloudArtifactTestData.PublicKey1, first.PublicKey);
    }

    [Fact]
    public void RejectsAnEmptyTrustSet()
    {
        var error = Assert.Throws<CloudArtifactException>(() => TrustSet.Parse("[]"u8));
        Assert.Equal(CloudArtifactErrorCodes.TrustSetInvalid, error.Code);
        Assert.Throws<CloudArtifactException>(() => TrustSet.FromKeys([]));
    }

    [Fact]
    public void RejectsAJsonObjectInsteadOfAnArray()
    {
        var error = Assert.Throws<CloudArtifactException>(() => TrustSet.Parse("""{"keys":[]}"""u8));
        Assert.Equal(CloudArtifactErrorCodes.TrustSetInvalid, error.Code);
    }

    [Fact]
    public void RejectsMalformedJson()
    {
        var error = Assert.Throws<CloudArtifactException>(() => TrustSet.Parse("""[{"key_id":}]"""u8));
        Assert.Equal(CloudArtifactErrorCodes.TrustSetInvalid, error.Code);
    }

    [Fact]
    public void RejectsDuplicateKeyIds()
    {
        var entry = CloudArtifactTestData.DefaultTrustEntry(CloudArtifactTestData.Seed1);
        var error = Assert.Throws<CloudArtifactException>(() =>
            TrustSet.Parse(Encoding.UTF8.GetBytes(CloudArtifactTestData.TrustJson(entry, entry))));
        Assert.Equal(CloudArtifactErrorCodes.TrustSetInvalid, error.Code);
    }

    [Fact]
    public void RejectsMismatchedKeyId()
    {
        var json = $"[{{\"key_id\":\"{CloudArtifactTestData.KeyId2}\",\"public_key\":\"{CloudArtifactTestData.PublicKey1Base64}\"}}]";
        var error = Assert.Throws<CloudArtifactException>(() => TrustSet.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(CloudArtifactErrorCodes.TrustSetInvalid, error.Code);
    }

    [Fact]
    public void RejectsNonCanonicalBase64()
    {
        var publicKey = Convert.ToBase64String(CloudArtifactTestData.PublicKey1);
        var json = $"[{{\"key_id\":\"{CloudArtifactTestData.KeyId1}\",\"public_key\":\"{publicKey.TrimEnd('=')}\"}}]";
        var error = Assert.Throws<CloudArtifactException>(() => TrustSet.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(CloudArtifactErrorCodes.TrustSetInvalid, error.Code);
    }

    [Fact]
    public void RejectsUnknownFields()
    {
        var json = $"[{{\"key_id\":\"{CloudArtifactTestData.KeyId1}\",\"public_key\":\"{CloudArtifactTestData.PublicKey1Base64}\",\"extra\":1}}]";
        var error = Assert.Throws<CloudArtifactException>(() => TrustSet.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(CloudArtifactErrorCodes.TrustSetInvalid, error.Code);
    }

    [Fact]
    public void ExpiredKeyIsNotUnexpired()
    {
        var notAfter = CloudArtifactTestData.Now.AddSeconds(-1);
        var json = CloudArtifactTestData.TrustJson(
            CloudArtifactTestData.DefaultTrustEntry(CloudArtifactTestData.Seed1, notAfter));
        var trust = TrustSet.Parse(Encoding.UTF8.GetBytes(json));

        Assert.False(trust.TryFindUnexpired(CloudArtifactTestData.KeyId1, CloudArtifactTestData.Now, out _));
        Assert.True(trust.TryFind(CloudArtifactTestData.KeyId1, out _));
    }

    [Fact]
    public void NotAfterBoundaryIsExclusive()
    {
        var notAfter = CloudArtifactTestData.Now;
        var json = CloudArtifactTestData.TrustJson(
            CloudArtifactTestData.DefaultTrustEntry(CloudArtifactTestData.Seed1, notAfter));
        var trust = TrustSet.Parse(Encoding.UTF8.GetBytes(json));

        Assert.False(trust.TryFindUnexpired(CloudArtifactTestData.KeyId1, notAfter, out _));
        Assert.True(trust.TryFindUnexpired(CloudArtifactTestData.KeyId1, notAfter.AddTicks(-1), out _));
    }

    [Fact]
    public void FromKeysValidatesKeyIdsAndLengths()
    {
        var error = Assert.Throws<CloudArtifactException>(() => TrustSet.FromKeys([
            new TrustedKey
            {
                KeyId = CloudArtifactTestData.KeyId2,
                PublicKey = CloudArtifactTestData.PublicKey1,
            },
        ]));
        Assert.Equal(CloudArtifactErrorCodes.TrustSetInvalid, error.Code);

        Assert.Throws<CloudArtifactException>(() => TrustSet.FromKeys([
            new TrustedKey
            {
                KeyId = CloudArtifactTestData.KeyId1,
                PublicKey = CloudArtifactTestData.PublicKey1[..31],
            },
        ]));
    }
}
