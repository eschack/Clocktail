using Clocktail.Domain.Broker;
namespace Clocktail.Tests.Domain;
public class MeasurementTrayStateTests
{
    [Fact]
    public void Measurement_is_explicit_and_release_waits_for_verification_without_masking_failures()
    {
        var now = DateTimeOffset.UtcNow;
        var refresh = new StatusRefresh(1, null, new(RestorationState.Ready, RestorationState.Ready, 1, null), true, true, null, "Measurement in progress");
        var d = new StatusDevice("gpu", "GPU", null, null, null, true, true, null, null, false, null, null, [], refresh);
        TrayState State(StatusDevice value, bool connected = true) => TrayStatus.State(value, connected, now, now);
        Assert.Equal(TrayState.MeasurementPaused, State(d));
        Assert.Equal(TrayState.VerificationPending, State(d with { Refresh = refresh with { MeasurementBusy = false } }));
        Assert.Equal(TrayState.Unavailable, State(d, false));
        Assert.Equal(TrayState.Failed, State(d with { Errors = ["broken record"] }));
        Assert.Equal(TrayState.Recovering, State(d with { Refresh = refresh with { Restoration = refresh.Restoration with { Recovery = RestorationState.Pending } } }));
        Assert.Equal(TrayState.Failed, State(d with { Refresh = refresh with { Restoration = refresh.Restoration with { Recovery = RestorationState.Failed } } }));
    }
}
