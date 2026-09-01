using System.Numerics;
using System.Security.Cryptography;

namespace Svrn7.Trust.KeyWallet;

/// <summary>
/// Minimal P-256 (secp256r1/NIST P-256) point arithmetic implemented from
/// the published domain parameters, used ONLY to compute a public key
/// point Q = d*G from a seed-derived private scalar d (needed because
/// recovering a key from a mnemonic requires deriving Q ourselves --
/// .NET's ECDsa.Create() only generates random keys, it doesn't derive
/// a key pair from an externally supplied scalar).
///
/// TRANSPARENCY NOTE: this is hand-written scalar multiplication using
/// double-and-add. It is NOT constant-time, so it should not be treated
/// as a general-purpose EC library or used anywhere signatures happen
/// repeatedly with secret data (timing side-channel risk). Here it runs
/// once per wallet creation/recovery, deriving a public value, which is
/// a low-risk use, but flagging the limitation rather than glossing over
/// it. Curve constants below were pulled verbatim from the Botan crypto
/// library's ec_named.cpp (a maintained, widely-used C++ crypto library)
/// rather than typed from memory, to reduce transcription-error risk --
/// but they were not independently verified against a second source, so
/// treat SelfTest() below as the real guarantee, not this comment.
/// </summary>
public static class EcMath
{
    // NIST P-256 domain parameters (verbatim from Botan's ec_named.cpp)
    private static readonly BigInteger P = BigInteger.Parse("00FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger A = BigInteger.Parse("00FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFC", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger B = BigInteger.Parse("005AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger Gx = BigInteger.Parse("006B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger Gy = BigInteger.Parse("004FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5", System.Globalization.NumberStyles.HexNumber);
    public static readonly BigInteger Order = BigInteger.Parse("00FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", System.Globalization.NumberStyles.HexNumber);

    public readonly record struct Point(BigInteger X, BigInteger Y, bool IsInfinity)
    {
        public static readonly Point Infinity = new(0, 0, true);
    }

    private static readonly Point G = new(Gx, Gy, false);

    // Fixed iteration count for ScalarMultiply's ladder -- 256 for P-256's
    // order. Using the order's bit length (not the actual scalar's) is what
    // keeps the loop count independent of the secret scalar's magnitude.
    private static readonly int OrderBitLength = (int)Order.GetBitLength();

    private static BigInteger Mod(BigInteger a, BigInteger m)
    {
        var r = a % m;
        return r.Sign < 0 ? r + m : r;
    }

    private static BigInteger ModInverse(BigInteger a, BigInteger modulus)
    {
        // modulus (P) is prime, so Fermat's little theorem applies: a^(p-2) mod p == a^-1 mod p
        return BigInteger.ModPow(Mod(a, modulus), modulus - 2, modulus);
    }

    private static Point Add(Point p1, Point p2)
    {
        if (p1.IsInfinity) return p2;
        if (p2.IsInfinity) return p1;

        if (p1.X == p2.X && Mod(p1.Y + p2.Y, P) == 0)
            return Point.Infinity;

        BigInteger lambda;
        if (p1.X == p2.X && p1.Y == p2.Y)
        {
            // Point doubling: lambda = (3x^2 + a) / (2y)
            var numerator = Mod(3 * p1.X * p1.X + A, P);
            var denominator = ModInverse(Mod(2 * p1.Y, P), P);
            lambda = Mod(numerator * denominator, P);
        }
        else
        {
            // Point addition: lambda = (y2 - y1) / (x2 - x1)
            var numerator = Mod(p2.Y - p1.Y, P);
            var denominator = ModInverse(Mod(p2.X - p1.X, P), P);
            lambda = Mod(numerator * denominator, P);
        }

        var x3 = Mod(lambda * lambda - p1.X - p2.X, P);
        var y3 = Mod(lambda * (p1.X - x3) - p1.Y, P);
        return new Point(x3, y3, false);
    }

    /// <summary>
    /// Arithmetic (branch-free) select: returns <paramref name="ifTrue"/> when
    /// <paramref name="condition"/> is true, else <paramref name="ifFalse"/>,
    /// via a bitmask AND/XOR rather than an if/else on the condition. Used so
    /// ScalarMultiply's ladder never branches on secret scalar bits.
    /// </summary>
    private static BigInteger Select(bool condition, BigInteger ifFalse, BigInteger ifTrue)
    {
        var mask = condition ? BigInteger.MinusOne : BigInteger.Zero; // all-1s or all-0s in two's complement
        return ifFalse ^ (mask & (ifFalse ^ ifTrue));
    }

    private static Point SelectPoint(bool condition, Point ifFalse, Point ifTrue)
    {
        return new Point(
            Select(condition, ifFalse.X, ifTrue.X),
            Select(condition, ifFalse.Y, ifTrue.Y),
            // IsInfinity is a cheap struct-field pick, not a gate on modular
            // arithmetic (ModInverse/Add), so it isn't the side-channel this
            // ladder is defending against.
            condition ? ifTrue.IsInfinity : ifFalse.IsInfinity);
    }

    private static void ConditionalSwap(bool swap, ref Point a, ref Point b)
    {
        var newA = SelectPoint(swap, ifFalse: a, ifTrue: b);
        var newB = SelectPoint(swap, ifFalse: b, ifTrue: a);
        a = newA;
        b = newB;
    }

    /// <summary>
    /// Scalar multiplication k*P via a Montgomery ladder: a fixed number of
    /// iterations (the curve order's bit length, not the scalar's), doing
    /// exactly one Add and one doubling every iteration regardless of the
    /// bit value, with register selection done by arithmetic mask instead of
    /// a branch. This removes the dominant timing leak in the previous
    /// double-and-add implementation (expensive-operation count and loop
    /// length both tracked the secret scalar's Hamming weight/bit length),
    /// which matters here because this function is called directly on the
    /// private scalar in KeyPair.FromSeed.
    ///
    /// This is a mitigation, not a hardened constant-time guarantee:
    /// System.Numerics.BigInteger's own arithmetic (Mod, ModInverse's
    /// ModPow, multiplication) is not a certified constant-time primitive,
    /// so residual timing variance from the underlying bignum implementation
    /// is still possible. Treat this the same way the class-level comment
    /// treats SelfTest(): correctness is verified, constant-time-ness is a
    /// best-effort property, not a proof.
    /// </summary>
    public static Point ScalarMultiply(BigInteger k, Point point)
    {
        var scalar = Mod(k, Order);
        var r0 = Point.Infinity;
        var r1 = point;

        for (var i = OrderBitLength - 1; i >= 0; i--)
        {
            var bit = ((scalar >> i) & BigInteger.One) == BigInteger.One;

            ConditionalSwap(bit, ref r0, ref r1);
            r1 = Add(r0, r1);
            r0 = Add(r0, r0);
            ConditionalSwap(bit, ref r0, ref r1);
        }

        return r0;
    }

    public static Point ScalarMultiplyBasePoint(BigInteger k) => ScalarMultiply(k, G);

    public static byte[] ToFixedBytes(BigInteger value, int length)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == length) return bytes;
        if (bytes.Length > length) throw new InvalidOperationException("Value too large for fixed-length encoding.");

        var padded = new byte[length];
        Buffer.BlockCopy(bytes, 0, padded, length - bytes.Length, bytes.Length);
        return padded;
    }

    /// <summary>
    /// Cross-checks this file's scalar multiplication against .NET's own
    /// ECDsa implementation using freshly generated random keys: generate
    /// a real key pair with ECDsa.Create(), then independently recompute
    /// Q = d*G with the code above and compare. Returns false immediately
    /// on any mismatch. Run automatically at startup before any mnemonic
    /// operation is allowed -- if this ever returns false, do NOT trust
    /// FromSeed()-derived keys on this machine/runtime.
    /// </summary>
    public static bool SelfTest(int iterations = 5)
    {
        for (var i = 0; i < iterations; i++)
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var parameters = ecdsa.ExportParameters(includePrivateParameters: true);

            var d = new BigInteger(parameters.D!, isUnsigned: true, isBigEndian: true);
            var expectedX = new BigInteger(parameters.Q.X!, isUnsigned: true, isBigEndian: true);
            var expectedY = new BigInteger(parameters.Q.Y!, isUnsigned: true, isBigEndian: true);

            var computed = ScalarMultiplyBasePoint(d);

            if (computed.IsInfinity || computed.X != expectedX || computed.Y != expectedY)
                return false;
        }
        return true;
    }
}
