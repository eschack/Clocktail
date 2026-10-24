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

    /// <summary>After a run that saw a driver reset, a job puts the daily profile back only once the service has recorded
    /// the crash (the trial it wrote is out of force), or after 30 s; no answer from the service is not a record.</summary>
    [Fact]
    public void After_a_reset_a_job_waits_for_the_service_to_record_the_crash()
    {
        const string written = "2026-10-24T10:00:00";
        var answers = new Queue<(string?, bool)>([(written, true), (null, false), (written, true), (null, true)]);
        var slept = TimeSpan.Zero;
        Assert.True(LabJob.Recorded(() => answers.Dequeue(), written, TimeSpan.FromSeconds(30), t => slept += t));
        Assert.Equal(TimeSpan.FromSeconds(3), slept);   // the service that did not answer once did not end the wait

        int asked = 0;
        Assert.False(LabJob.Recorded(() => { asked++; return (null, false); }, written, LabJob.CrashRecordWait, _ => { }));
        Assert.Equal(31, asked);
    }
}
