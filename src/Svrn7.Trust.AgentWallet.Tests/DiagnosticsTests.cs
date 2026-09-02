using System.Diagnostics;
using System.Diagnostics.Metrics;
using Svrn7.Trust.AgentWallet;

namespace AgentWallet.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void Source_And_Meter_AreNamedAgentWallet()
    {
        Assert.Equal("AgentWallet", AgentWalletDiagnostics.SourceName);
        Assert.Equal("AgentWallet", AgentWalletDiagnostics.ActivitySource.Name);
        Assert.Equal("AgentWallet", AgentWalletDiagnostics.Meter.Name);
    }

    [Fact]
    public void Recording_WithNoListener_IsANoOp()
    {
        // No ActivityListener / MeterListener attached — every call must be safe.
        AgentWalletDiagnostics.RecordUnlockResult(null, AgentWalletResult.Success);
        AgentWalletDiagnostics.RecordWalletWritten("create");
        AgentWalletDiagnostics.RecordPinCheck(PinCheck.FirstUse);
        AgentWalletDiagnostics.RecordKdfDuration(Stopwatch.GetTimestamp(), AgentWalletResult.Success);
    }

    [Fact]
    public void UnlockCounter_IsObservable_ByAListener()
    {
        long sum = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "AgentWallet" && inst.Name == "agentwallet.unlock.total")
                l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((inst, measurement, tags, state) => sum += measurement);
        listener.Start();

        AgentWalletDiagnostics.RecordUnlockResult(null, AgentWalletResult.WrongPassword);

        listener.Dispose();
        Assert.Equal(1, sum);
    }
}
