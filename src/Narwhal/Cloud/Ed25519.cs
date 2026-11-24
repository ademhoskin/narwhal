// SPDX-License-Identifier: MIT
// Copyright (c) 2026 narwhal contributors
//
// Managed Ed25519 signature verification for RFC 8032 (Edwards-Curve Digital
// Signature Algorithm). The implementation uses the twisted Edwards addition
// and doubling formulas from RFC 8032 section 5.1 and System.Numerics.BigInteger
// for field arithmetic. It needs no native library and is AOT safe.

using System.Numerics;
using System.Security.Cryptography;

namespace Narwhal.Cloud;

/// <summary>RFC 8032 Ed25519 verification over a 32-byte public key and a 64-byte signature.</summary>
internal static class Ed25519
{
    private static readonly BigInteger P = (BigInteger.One << 255) - 19;
    private static readonly BigInteger L =
        BigInteger.Parse("7237005577332262213973186563042994240857116359379907606001950938285454250989");
    private static readonly BigInteger D =
        BigInteger.Parse("37095705934669439343138083508754565189542113879843219016388785533085940283555");
    private static readonly BigInteger SqrtMinusOne =
        BigInteger.Parse("19681161376707505956807079304988542015446066515923890162744021073123829784752");

    private static readonly Point Base = new(
        BigInteger.Parse("15112221349535400772501151409588531511454012693041857206046113283949847762202"),
        BigInteger.Parse("46316835694926478169428394003475163141307993866256225615783033603165251855960"),
        BigInteger.One,
        BigInteger.Parse("46316835694926478169428394003475163141307993866256225615783033603165251855960") *
        BigInteger.Parse("15112221349535400772501151409588531511454012693041857206046113283949847762202") % P);

    /// <summary>Returns true only for a canonical 32-byte key, canonical 64-byte signature, and a valid equation.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64)
            return false;

        if (!TryDecodePoint(publicKey, out var a) || IsSmallOrder(a))
            return false;
        if (!TryDecodePoint(signature[..32], out var r))
            return false;

        var s = FromLittleEndian(signature[32..]);
        if (s >= L)
            return false;

        var hashInput = new byte[64 + message.Length];
        signature[..32].CopyTo(hashInput);
        publicKey.CopyTo(hashInput.AsSpan(32));
        message.CopyTo(hashInput.AsSpan(64));
        var k = FromLittleEndian(SHA512.HashData(hashInput)) % L;

        var left = Multiply(s, Base);
        var right = Add(r, Multiply(k, a));
        return IsIdentity(Add(left, right.Negate()));
    }

    private static BigInteger Mod(BigInteger value)
    {
        var result = value % P;
        return result.Sign < 0 ? result + P : result;
    }

    private static BigInteger FromLittleEndian(ReadOnlySpan<byte> bytes) =>
        new(bytes, isUnsigned: true, isBigEndian: false);

    private static byte[] ToLittleEndian32(BigInteger value)
    {
        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        var result = new byte[32];
        raw.AsSpan(0, Math.Min(raw.Length, 32)).CopyTo(result);
        return result;
    }

    private static bool TryDecodePoint(ReadOnlySpan<byte> encoded, out Point point)
    {
        point = default;
        if (encoded.Length != 32)
            return false;

        Span<byte> yBytes = stackalloc byte[32];
        encoded.CopyTo(yBytes);
        var sign = (yBytes[31] >> 7) & 1;
        yBytes[31] &= 0x7f;

        var y = FromLittleEndian(yBytes);
        if (y >= P)
            return false;

        var y2 = Mod(y * y);
        var u = Mod(y2 - 1);
        var v = Mod(D * y2 + 1);
        var v3 = Mod(v * v * v);
        var v7 = Mod(v3 * v3 * v);
        var x = Mod(u * v3 * BigInteger.ModPow(Mod(u * v7), (P - 5) / 8, P));
        var vx2 = Mod(v * x * x);

        if (vx2 == u)
        {
            // x is a square root of u/v.
        }
        else if (Mod(vx2 + u).IsZero)
        {
            x = Mod(x * SqrtMinusOne);
        }
        else
        {
            return false;
        }

        if (x.IsZero && sign == 1)
            return false;

        var xIsOdd = !x.IsEven;
        if (xIsOdd != (sign == 1))
            x = Mod(-x);

        point = new Point(x, y, BigInteger.One, Mod(x * y));
        return true;
    }

    private static byte[] EncodePoint(Point point)
    {
        var zInverse = BigInteger.ModPow(point.Z, P - 2, P);
        var x = Mod(point.X * zInverse);
        var y = Mod(point.Y * zInverse);
        var encoded = ToLittleEndian32(y);
        if (!x.IsEven)
            encoded[31] |= 0x80;
        return encoded;
    }

    private static Point Add(Point left, Point right)
    {
        var a = Mod((left.Y - left.X) * (right.Y - right.X));
        var b = Mod((left.Y + left.X) * (right.Y + right.X));
        var c = Mod(2 * D * left.T * right.T);
        var d = Mod(2 * left.Z * right.Z);
        var e = Mod(b - a);
        var f = Mod(d - c);
        var g = Mod(d + c);
        var h = Mod(b + a);
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Double(Point point)
    {
        var a = Mod(point.X * point.X);
        var b = Mod(point.Y * point.Y);
        var c = Mod(2 * point.Z * point.Z);
        var d = Mod(-a);
        var e = Mod(Mod((point.X + point.Y) * (point.X + point.Y)) - a - b);
        var g = Mod(d + b);
        var f = Mod(g - c);
        var h = Mod(d - b);
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Multiply(BigInteger scalar, Point point)
    {
        var result = Point.Identity;
        var addend = point;
        while (scalar > BigInteger.Zero)
        {
            if (!scalar.IsEven)
                result = Add(result, addend);
            addend = Double(addend);
            scalar >>= 1;
        }

        return result;
    }

    private static bool IsIdentity(Point point) =>
        Mod(point.X).IsZero && Mod(point.Y - point.Z).IsZero && !Mod(point.Z).IsZero;

    private static bool IsSmallOrder(Point point) => IsIdentity(Multiply(8, point));

    private readonly struct Point(BigInteger x, BigInteger y, BigInteger z, BigInteger t)
    {
        public BigInteger X { get; } = x;
        public BigInteger Y { get; } = y;
        public BigInteger Z { get; } = z;
        public BigInteger T { get; } = t;

        public static Point Identity { get; } = new(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero);

        public Point Negate() => new(Mod(-X), Y, Z, Mod(-T));
    }

    /// <summary>Derives the RFC 8032 public key from a 32-byte Ed25519 seed.</summary>
    internal static byte[] PublicKeyFromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32)
            throw new ArgumentException("Ed25519 seed must be 32 bytes", nameof(seed));

        var expanded = SHA512.HashData(seed);
        var scalarBytes = expanded.AsSpan(0, 32).ToArray();
        scalarBytes[0] &= 248;
        scalarBytes[31] &= 127;
        scalarBytes[31] |= 64;
        return EncodePoint(Multiply(FromLittleEndian(scalarBytes), Base));
    }

    /// <summary>Signs a message with the RFC 8032 deterministic signing procedure.</summary>
    internal static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        if (seed.Length != 32)
            throw new ArgumentException("Ed25519 seed must be 32 bytes", nameof(seed));

        var expanded = SHA512.HashData(seed);
        var scalarBytes = expanded.AsSpan(0, 32).ToArray();
        scalarBytes[0] &= 248;
        scalarBytes[31] &= 127;
        scalarBytes[31] |= 64;
        var scalar = FromLittleEndian(scalarBytes);
        var publicKey = PublicKeyFromSeed(seed);

        var nonceInput = new byte[32 + message.Length];
        expanded.AsSpan(32, 32).CopyTo(nonceInput);
        message.CopyTo(nonceInput.AsSpan(32));
        var nonce = FromLittleEndian(SHA512.HashData(nonceInput)) % L;
        var encodedR = EncodePoint(Multiply(nonce, Base));

        var challengeInput = new byte[64 + message.Length];
        encodedR.CopyTo(challengeInput, 0);
        publicKey.CopyTo(challengeInput, 32);
        message.CopyTo(challengeInput.AsSpan(64));
        var challenge = FromLittleEndian(SHA512.HashData(challengeInput)) % L;

        var s = (nonce + challenge * scalar) % L;
        var signature = new byte[64];
        encodedR.CopyTo(signature, 0);
        ToLittleEndian32(s).CopyTo(signature, 32);
        return signature;
    }
}
