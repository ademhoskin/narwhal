using Narwhal.Cloud;
using Xunit;

namespace Narwhal.Tests;

public class CloudArtifactEd25519Tests
{
    private sealed record Vector(string Secret, string PublicKey, string Message, string Signature);

    // RFC 8032 section 7.1, TEST 1 through TEST 3.
    private static readonly Vector[] Rfc8032Vectors =
    [
        new(
            "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
            "",
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"),
        new(
            "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
            "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
            "72",
            "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"),
        new(
            "c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
            "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
            "af82",
            "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a"),
    ];

    public static TheoryData<int> VectorIndexes => [0, 1, 2];

    [Theory]
    [MemberData(nameof(VectorIndexes))]
    public void Rfc8032VectorsVerify(int index)
    {
        var vector = Rfc8032Vectors[index];
        var publicKey = Convert.FromHexString(vector.PublicKey);
        var message = Convert.FromHexString(vector.Message);
        var signature = Convert.FromHexString(vector.Signature);

        Assert.True(Ed25519.Verify(publicKey, message, signature));
    }

    [Theory]
    [MemberData(nameof(VectorIndexes))]
    public void Rfc8032VectorsSign(int index)
    {
        var vector = Rfc8032Vectors[index];
        var seed = Convert.FromHexString(vector.Secret);
        var message = Convert.FromHexString(vector.Message);

        Assert.Equal(vector.PublicKey, Convert.ToHexStringLower(Ed25519.PublicKeyFromSeed(seed)));
        Assert.Equal(vector.Signature, Convert.ToHexStringLower(Ed25519.Sign(seed, message)));
    }

    [Fact]
    public void RejectsTamperedMessage()
    {
        var vector = Rfc8032Vectors[1];
        var publicKey = Convert.FromHexString(vector.PublicKey);
        var signature = Convert.FromHexString(vector.Signature);

        Assert.False(Ed25519.Verify(publicKey, [0x73], signature));
    }

    [Fact]
    public void RejectsTamperedSignature()
    {
        var vector = Rfc8032Vectors[1];
        var publicKey = Convert.FromHexString(vector.PublicKey);
        var signature = Convert.FromHexString(vector.Signature);
        signature[0] ^= 0x01;

        Assert.False(Ed25519.Verify(publicKey, [0x72], signature));
    }

    [Fact]
    public void RejectsNonCanonicalS()
    {
        var seed = Convert.FromHexString(Rfc8032Vectors[0].Secret);
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var signature = Ed25519.Sign(seed, []);
        var nonCanonical = (byte[])signature.Clone();

        // Replace S with L, which is mathematically zero but not a canonical encoding.
        var l = Convert.FromHexString("edd3f55c1a631258d69cf7a2def9de1400000000000000000000000000000010");
        l.CopyTo(nonCanonical, 32);

        Assert.False(Ed25519.Verify(publicKey, [], nonCanonical));
    }

    [Fact]
    public void RejectsWrongLengths()
    {
        var seed = Convert.FromHexString(Rfc8032Vectors[0].Secret);
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var signature = Ed25519.Sign(seed, [0x01]);

        Assert.False(Ed25519.Verify(publicKey[..31], [0x01], signature));
        Assert.False(Ed25519.Verify(publicKey, [0x01], signature[..63]));
        Assert.False(Ed25519.Verify([], [0x01], signature));
    }

    [Fact]
    public void RejectsInvalidPublicKeyEncodings()
    {
        var seed = Convert.FromHexString(Rfc8032Vectors[0].Secret);
        var signature = Ed25519.Sign(seed, [0x01]);

        var nonCanonicalY = new byte[32];
        nonCanonicalY[31] = 0x7f;
        for (var i = 0; i < 31; i++)
            nonCanonicalY[i] = 0xff;
        Assert.False(Ed25519.Verify(nonCanonicalY, [0x01], signature));
        Assert.False(Ed25519.Verify(new byte[32], [0x01], signature));
    }

    [Fact]
    public void PublicKeyFromSeedMatchesFixedVector()
    {
        var seed = Convert.FromHexString(Rfc8032Vectors[2].Secret);

        Assert.Equal(Rfc8032Vectors[2].PublicKey, Convert.ToHexStringLower(Ed25519.PublicKeyFromSeed(seed)));
    }

    [Fact]
    public void RejectsFlippedSignatureBit()
    {
        var vector = Rfc8032Vectors[1];
        var publicKey = Convert.FromHexString(vector.PublicKey);
        var message = Convert.FromHexString(vector.Message);

        foreach (var index in new[] { 0, 31, 32, 63 })
        {
            var signature = Convert.FromHexString(vector.Signature);
            signature[index] ^= 0x01;
            Assert.False(Ed25519.Verify(publicKey, message, signature));
        }
    }

    [Fact]
    public void RejectsTruncatedOrOversizedSignature()
    {
        var seed = Convert.FromHexString(Rfc8032Vectors[0].Secret);
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var signature = Ed25519.Sign(seed, [0x01]);
        var oversized = new byte[65];
        signature.CopyTo(oversized, 0);

        Assert.False(Ed25519.Verify(publicKey, [0x01], []));
        Assert.False(Ed25519.Verify(publicKey, [0x01], signature[..63]));
        Assert.False(Ed25519.Verify(publicKey, [0x01], oversized));
    }

    [Fact]
    public void RejectsWrongLengthPublicKey()
    {
        var seed = Convert.FromHexString(Rfc8032Vectors[0].Secret);
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var signature = Ed25519.Sign(seed, [0x01]);
        var oversized = new byte[33];
        publicKey.CopyTo(oversized, 0);

        Assert.False(Ed25519.Verify([], [0x01], signature));
        Assert.False(Ed25519.Verify(publicKey[..31], [0x01], signature));
        Assert.False(Ed25519.Verify(oversized, [0x01], signature));
    }

    [Fact]
    public void RoundTripsRandomMessages()
    {
        var random = new Random(12345);
        for (var i = 0; i < 8; i++)
        {
            var seed = new byte[32];
            random.NextBytes(seed);
            var message = new byte[random.Next(0, 256)];
            random.NextBytes(message);

            var publicKey = Ed25519.PublicKeyFromSeed(seed);
            var signature = Ed25519.Sign(seed, message);

            Assert.True(Ed25519.Verify(publicKey, message, signature));
        }
    }
}
