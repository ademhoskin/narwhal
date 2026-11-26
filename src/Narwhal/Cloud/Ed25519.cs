// SPDX-License-Identifier: MIT
// Copyright (c) 2026 narwhal contributors
//
// Ed25519 (RFC 8032) over BouncyCastle's managed curve implementation. The
// wrapper fixes the byte lengths and exposes the fail-closed verification the
// rest of the codebase expects.

using BcEd25519 = Org.BouncyCastle.Math.EC.Rfc8032.Ed25519;

namespace Narwhal.Cloud;

/// <summary>RFC 8032 Ed25519 verification over a 32-byte public key and a 64-byte signature.</summary>
internal static class Ed25519
{
    /// <summary>Returns true only for canonical inputs of the expected length and a valid equation.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64)
            return false;

        return BcEd25519.Verify(signature, publicKey, message);
    }

    /// <summary>Derives the RFC 8032 public key from a 32-byte Ed25519 seed.</summary>
    internal static byte[] PublicKeyFromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32)
            throw new ArgumentException("Ed25519 seed must be 32 bytes", nameof(seed));

        var publicKey = new byte[32];
        BcEd25519.GeneratePublicKey(seed, publicKey);
        return publicKey;
    }

    /// <summary>Signs a message with the RFC 8032 deterministic signing procedure.</summary>
    internal static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        if (seed.Length != 32)
            throw new ArgumentException("Ed25519 seed must be 32 bytes", nameof(seed));

        var signature = new byte[64];
        BcEd25519.Sign(seed, message, signature);
        return signature;
    }
}
