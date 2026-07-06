using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed class ExecutionRealismModelTests
{
    [Fact]
    public void CapQuantityByParticipation_LimitsToShareOfBarVolume()
    {
        // 10% of a 2,000-share bar = 200 fillable, even though the strategy wanted 5,000.
        var capped = ExecutionRealismModel.CapQuantityByParticipation(5000, entryBarVolume: 2000m, maxParticipationPct: 10m);
        Assert.Equal(200, capped);
    }

    [Fact]
    public void CapQuantityByParticipation_DisabledWhenPctOrVolumeMissing_ReturnsRequested()
    {
        Assert.Equal(5000, ExecutionRealismModel.CapQuantityByParticipation(5000, 2000m, 0m));
        Assert.Equal(5000, ExecutionRealismModel.CapQuantityByParticipation(5000, 0m, 10m));
    }

    [Fact]
    public void CapQuantityByParticipation_NeverRaisesRequestedQuantity()
    {
        // Plenty of liquidity: cap must not increase the requested size.
        var capped = ExecutionRealismModel.CapQuantityByParticipation(100, entryBarVolume: 1_000_000m, maxParticipationPct: 10m);
        Assert.Equal(100, capped);
    }

    [Fact]
    public void RegulatoryFees_AddsSecAndCappedTaf()
    {
        // SEC: 100000 * 0.0000278 = 2.78; TAF: 1000 * 0.000166 = 0.166 (under cap).
        var fees = ExecutionRealismModel.RegulatoryFees(
            sellProceeds: 100_000m,
            shares: 1000,
            secFeeRate: 0.0000278m,
            finraTafPerShare: 0.000166m,
            finraTafCap: 8.30m);
        Assert.Equal(2.78m + 0.166m, fees, precision: 6);
    }

    [Fact]
    public void RegulatoryFees_CapsTaf()
    {
        // 1,000,000 shares * 0.000166 = 166, capped to 8.30. No SEC rate here.
        var fees = ExecutionRealismModel.RegulatoryFees(
            sellProceeds: 0m,
            shares: 1_000_000,
            secFeeRate: 0m,
            finraTafPerShare: 0.000166m,
            finraTafCap: 8.30m);
        Assert.Equal(8.30m, fees);
    }

    [Fact]
    public void RegulatoryFees_AllZero_IsFree()
    {
        var fees = ExecutionRealismModel.RegulatoryFees(100_000m, 1000, 0m, 0m, 0m);
        Assert.Equal(0m, fees);
    }
}
