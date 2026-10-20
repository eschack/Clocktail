using System.IO.Pipes;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>
/// The framing and a real pipe round-trip, in process. The pipe is cross-platform, so the loop, the length
/// framing and the request/response envelopes can be proven here; only the Windows ACL and the service wrapper
/// wait for the device. If ping's small result crosses intact, a bigger op result crosses by the same path.
/// </summary>
[Collection("fake-driver")]
public class BrokerTransportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();

    public BrokerTransportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Target : IGpuTarget, IGpuTargets
    {
        private readonly OwnershipJournal _j;
        private readonly ApplySentinel _s;
        public Target(OwnershipJournal j, ApplySentinel s) { _j = j; _s = s; }
        public string StableId => "gpu-A";
        public string Name => "fake";
        public GpuSession Session { get; } = new(1, "fake");
        public OwnershipJournal Journal => _j;
        public ApplySentinel Sentinel => _s;
        public IReadOnlyList<string> Ids => ["gpu-A"];
        public IGpuTarget? Resolve(string? id) => id == null || id == "gpu-A" ? this : null;
    }

    private BrokerDispatcher Dispatcher()
    {
        FakeDriver.Reset();
        var hw = new NvClient(FakeDriver.ApiFor([]));
        var t = new Target(new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A"),
                           new ApplySentinel(Path.Combine(_dir, "apply.lock")));
        return new BrokerDispatcher(hw, t, _host, canWrite: true);
    }

    [Fact]
    public void A_request_survives_the_framing()
    {
        using var ms = new MemoryStream();
        var req = new BrokerRequest(BrokerRequest.OpPower, GpuId: "g", Kind: "fe", Mw: 560_000, AllowCreate: true);
        BrokerTransport.WriteRequest(ms, req);
        ms.Position = 0;
        Assert.Equal(req, BrokerTransport.ReadRequest(ms));
    }

    [Fact]
    public void A_response_with_a_result_survives_the_framing()
    {
        using var ms = new MemoryStream();
        var resp = new BrokerResponse(true, Result: JsonSerializer.SerializeToElement(new { a = 1, b = "x" }));
        BrokerTransport.WriteResponse(ms, resp);
        ms.Position = 0;
        var back = BrokerTransport.ReadResponse(ms);
        Assert.True(back.Ok);
        Assert.Equal(1, back.Result!.Value.GetProperty("a").GetInt32());
        Assert.Equal("x", back.Result!.Value.GetProperty("b").GetString());
    }

    [Fact]
    public void An_oversized_frame_is_refused_before_its_body_is_read()
    {
        using var ms = new MemoryStream();
        ms.Write([0xff, 0xff, 0xff, 0x7f]);   // Int32.MaxValue length prefix, no body
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => BrokerTransport.ReadResponse(ms));
    }

    [Fact]
    public async Task Ping_round_trips_over_a_real_pipe()
    {
        var name = "clocktail-test-" + Guid.NewGuid().ToString("n");
        var server = new BrokerServer(() => new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous), Dispatcher());
        using var cts = new CancellationTokenSource();
        var loop = server.RunAsync(cts.Token);

        var r = await Task.Run(() => BrokerClient.TryCall(new BrokerRequest(BrokerRequest.OpPing), name));
        Assert.NotNull(r);
        Assert.True(r!.Ok);
        Assert.Equal("gpu-A", r.Result!.Value.GetProperty("gpus")[0].GetString());

        await cts.CancelAsync();
        await loop;
    }

    [Fact]
    public async Task An_unknown_op_round_trips_as_an_error_envelope()
    {
        var name = "clocktail-test-" + Guid.NewGuid().ToString("n");
        var server = new BrokerServer(() => new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous), Dispatcher());
        using var cts = new CancellationTokenSource();
        var loop = server.RunAsync(cts.Token);

        var r = await Task.Run(() => BrokerClient.TryCall(new BrokerRequest("frobnicate"), name));
        Assert.NotNull(r);
        Assert.False(r!.Ok);
        Assert.Contains("unknown operation", r.Error);

        await cts.CancelAsync();
        await loop;
    }

    [Fact]
    public async Task A_client_that_connects_and_never_speaks_does_not_hold_up_the_next_one()
    {
        var name = "clocktail-test-" + Guid.NewGuid().ToString("n");
        var server = new BrokerServer(() => new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous), Dispatcher());
        using var cts = new CancellationTokenSource();
        var loop = server.RunAsync(cts.Token);

        using var silent = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        await silent.ConnectAsync(2000);   // connected, sends nothing

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await Task.Run(() => BrokerClient.TryCall(new BrokerRequest(BrokerRequest.OpPing), name));
        Assert.NotNull(r);
        Assert.True(r!.Ok);
        Assert.True(sw.Elapsed < BrokerServer.RequestTimeout, $"the ping waited {sw.Elapsed} behind the silent client");

        await cts.CancelAsync();
        await loop;
    }

    [Fact]
    public void A_broker_that_is_not_there_returns_null()
    {
        var r = BrokerClient.TryCall(new BrokerRequest(BrokerRequest.OpPing), "clocktail-absent-" + Guid.NewGuid().ToString("n"), connectMs: 400);
        Assert.Null(r);
    }
}
