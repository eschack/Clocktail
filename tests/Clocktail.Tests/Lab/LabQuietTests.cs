using Clocktail.Domain.Broker;
using Clocktail.Lab;

namespace Clocktail.Tests.Lab;

/// <summary>A lab job asks the service for "quiet" before it measures: it goes once nothing is landing, asks again while it
/// cannot tell, and gives up on a refusal.</summary>
public class LabQuietTests
{
    [Fact]
    public void A_job_measures_once_nothing_is_landing_and_asks_again_while_it_cannot_tell()
    {
        Assert.Equal(LabJob.Settled.Go, LabJob.Quiet(() => new BrokerResponse(true)));
        Assert.Equal(LabJob.Settled.Go, LabJob.Quiet(() => null));   // no service to reach: nothing it writes
        Assert.Equal(LabJob.Settled.Go, LabJob.Quiet(() => new BrokerResponse(false, "experiment needs show, ...", Code: BrokerCode.BadRequest)));   // before 0.3.7

        Assert.Equal(LabJob.Settled.Again, LabJob.Quiet(() => new BrokerResponse(false, "another operation has held the card for over 30 s", Code: BrokerCode.WindowBusy)));
        // The request went out and the exchange broke: a write may still be landing. Until 0.3.7 this read as no service.
        Assert.Equal(LabJob.Settled.Again, LabJob.Quiet(() => throw new IOException("the pipe broke")));
        Assert.Equal(LabJob.Settled.Again, LabJob.Quiet(() => throw new TimeoutException("the Clocktail service took longer than 120 s to answer")));

        Assert.Equal(LabJob.Settled.No, LabJob.Quiet(() => new BrokerResponse(false, "no GPU is available", Code: BrokerCode.NoGpu)));
    }
}
