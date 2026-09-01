using System.Numerics;
using Svrn7.Trust.KeyWallet;
using Xunit;

namespace KeyWallet.Tests;

public class EcMathTests
{
    [Fact]
    public void SelfTest_PassesAcrossManyRandomScalars()
    {
        // The default SelfTest() only runs 5 iterations; run more here to
        // exercise the Montgomery-ladder rewrite of ScalarMultiply across a
        // wider sample of random scalars, cross-checked against .NET's own
        // ECDsa-derived Q = d*G.
        Assert.True(EcMath.SelfTest(50));
    }

    [Fact]
    public void ScalarMultiplyBasePoint_ZeroScalar_ReturnsInfinity()
    {
        Assert.True(EcMath.ScalarMultiplyBasePoint(BigInteger.Zero).IsInfinity);
    }

    [Fact]
    public void ScalarMultiplyBasePoint_ScalarEqualToOrder_ReturnsInfinity()
    {
        // k mod Order == 0 when k == Order itself -- exercises the ladder's
        // fixed-iteration-count path producing identity for a non-trivial
        // (but congruent-to-zero) input, not just a literal 0.
        Assert.True(EcMath.ScalarMultiplyBasePoint(EcMath.Order).IsInfinity);
    }

    [Fact]
    public void ScalarMultiplyBasePoint_NonZeroScalars_AreNeverInfinity()
    {
        Assert.False(EcMath.ScalarMultiplyBasePoint(BigInteger.One).IsInfinity);
        Assert.False(EcMath.ScalarMultiplyBasePoint(EcMath.Order - 1).IsInfinity);
    }
}
