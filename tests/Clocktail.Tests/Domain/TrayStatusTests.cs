using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Profiles;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public sealed class TrayStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-status-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeHost _host = new();
    private readonly Target _target;
    public TrayStatusTests()
    {
        Directory.CreateDirectory(_dir);
        _target = new(new(Path.Combine(_dir, "ownership.json"), _host, "gpu-test"), new(Path.Combine(_dir, "apply.lock")));
    }
    public void Dispose() => Directory.Delete(_dir, true);
    private sealed class Target(OwnershipJournal journal, ApplySentinel sentinel) : IGpuTarget, IGpuTargets
    {
        public string StableId => "gpu-test";
        public string Name => "Test GPU";
        public GpuSession Session { get; } = new(1, "test");
        public OwnershipJournal Journal => journal;
        public ApplySentinel Sentinel => sentinel;
        public IReadOnlyList<string> Ids => [StableId];
        public IGpuTarget? Resolve(string? id) => id == null || id == StableId ? this : null;
    }
    private BrokerDispatcher Dispatcher(NvClient? hw = null, Func<string?>? judge = null) =>
        new(hw ?? new NvClient(FakeDriver.ApiFor([])), _target, _host, canWrite: false, judgeBusyReason: judge);
    private static TrayStatus Status(BrokerDispatcher dispatcher, string kind = "cached")
    {
        var r = dispatcher.FromClient(new(BrokerRequest.OpStatus, GpuId: "gpu-test", Kind: kind, Value: "1"));
        Assert.True(r.Ok, r.Error);
        return r.Result!.Value.Deserialize<TrayStatus>()!;
    }
    private void SaveCore(int khz) => new DailyProfile([new("pstates20", "core", khz.ToString(System.Globalization.CultureInfo.InvariantCulture))], _host.Now)
        .Save(Path.Combine(_dir, DailyProfile.FileName));

    [Fact]
    public void Cached_status_reads_no_driver_and_never_settles_or_changes_records()
    {
        FakeDriver.Reset();
        SaveCore(240000);
        File.WriteAllText(_target.Sentinel.Path, "prior application requires recovery");
        var before = Directory.GetFiles(_dir).ToDictionary(p => p, File.ReadAllBytes);
        var dispatcher = Dispatcher();
        dispatcher.CachedTelemetry = _ => new(_host.Now, 0, null, null, 31, 33, 0, null);
        var snapshot = Status(dispatcher);
        Assert.Null(snapshot.Devices[0].Verification);
        Assert.Equal(33, snapshot.Devices[0].Telemetry?.TempC);
        Assert.Equal(TrayState.Unknown, TrayStatus.State(snapshot.Devices[0], true, snapshot.CapturedAt, _host.Now));
        Assert.Equal(0, FakeDriver.Gets);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_dir).Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"Writes\":null}")]
    public void Corrupt_saved_targets_are_unknown_instead_of_absent_or_applied(string json)
    {
        File.WriteAllText(Path.Combine(_dir, DailyProfile.FileName), json);
        var snapshot = Status(Dispatcher());
        Assert.False(snapshot.Devices[0].SavedDailyKnown);
        Assert.NotEmpty(snapshot.Devices[0].Errors);
        Assert.Equal(TrayState.Failed, TrayStatus.State(snapshot.Devices[0], true, snapshot.CapturedAt, _host.Now));
    }

    [Fact]
    public void Explicit_verification_can_match_global_offset_only_with_effective_curve_readback()
    {
        var fake = new FakeVf { GlobalKhz = 240000 };
        var dispatcher = Dispatcher(fake.Install());
        SaveCore(240000);
        var verified = Status(dispatcher, "verify");
        var verification = verified.Devices[0].Verification!;
        Assert.Equal(DailyComparison.Matched, verification.Comparison);
        Assert.Equal(127, verification.Curve.Count);
        Assert.Equal(1140, verification.Curve[0].EffectiveMhz);
        Assert.Equal(900, verification.Curve[0].StockMhz);
        Assert.Equal(0, FakeDriver.Sets);
        int gets = FakeDriver.Gets;
        var cached = Status(dispatcher);
        Assert.Equal(verification.At, cached.Devices[0].Verification?.At);
        Assert.Equal(gets, FakeDriver.Gets);
        // Even an attempted profile mutation invalidates prior verification.
        dispatcher.FromClient(new(BrokerRequest.OpProfile, Kind: "off"));
        Assert.Null(Status(dispatcher).Devices[0].Verification);
    }

    [Fact]
    public void P0_readback_matching_saved_offset_cannot_hide_the_known_effective_curve_disagreement()
    {
        var fake = new FakeVf { GlobalKhz = 240000 };
        fake.Effective = (i, _) => fake.Base(i);
        var dispatcher = Dispatcher(fake.Install());
        SaveCore(240000);
        var verified = Status(dispatcher, "verify");
        Assert.Equal(DailyComparison.Unknown, verified.Devices[0].Verification?.Comparison);
        Assert.NotNull(verified.Devices[0].Verification?.Error);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void Settings_difference_and_partial_reads_are_not_reported_as_applied()
    {
        var dispatcher = Dispatcher(new FakeVf().Install());
        SaveCore(240000);
        var snapshot = Status(dispatcher, "verify");
        Assert.Equal(DailyComparison.Different, snapshot.Devices[0].Verification?.Comparison);
        Assert.Contains(snapshot.Devices[0].Verification!.Settings, s => s.Value == null && s.Error != null);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public async Task Cached_status_does_not_wait_for_an_operation_and_verification_stands_down()
    {
        var dispatcher = Dispatcher();
        var operation = typeof(BrokerDispatcher).GetField("_operation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dispatcher)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => { lock (operation) { held.Set(); release.Wait(TimeSpan.FromSeconds(5)); } });
        Assert.True(held.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            Assert.True(Status(dispatcher).Devices[0].OperationInProgress);
            var response = dispatcher.FromClient(new(BrokerRequest.OpStatus, GpuId: "gpu-test", Kind: "verify"));
            Assert.Equal(BrokerCode.WindowBusy, response.Code);
        }
        finally { release.Set(); await holder; }
    }

    [Fact]
    public void A_measurement_lease_blocks_extra_verification_reads_but_not_cached_status()
    {
        FakeDriver.Reset();
        var dispatcher = Dispatcher(judge: () => "measurement in progress");
        var r = dispatcher.FromClient(new(BrokerRequest.OpStatus, GpuId: "gpu-test", Kind: "verify"));
        Assert.Equal(BrokerCode.JudgeBusy, r.Code);
        Assert.Single(Status(dispatcher).Devices);
        Assert.Equal(0, FakeDriver.Gets);
    }

    [Fact]
    public async Task Typed_status_crosses_a_real_fake_broker_pipe_without_driver_calls()
    {
        FakeDriver.Reset();
        string name = "clocktail-status-test-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = new BrokerServer(() => new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous), Dispatcher());
        var serving = server.ServeOneAsync(stop.Token);
        var reply = await BrokerClient.TryCallAsync(new(BrokerRequest.OpStatus, Kind: "cached", Value: "1"), stop.Token, name);
        await serving;
        Assert.True(reply?.Ok);
        Assert.Equal("gpu-test", reply!.Result!.Value.Deserialize<TrayStatus>()!.Devices[0].StableId);
        Assert.Equal(0, FakeDriver.Gets);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public async Task Canceling_an_interactive_request_closes_a_pipe_that_never_answers()
    {
        string name = "clocktail-status-cancel-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var stop = new CancellationTokenSource();
        var connect = server.WaitForConnectionAsync();
        var call = BrokerClient.TryCallAsync(new(BrokerRequest.OpStatus, Kind: "cached"), stop.Token, name);
        await connect.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Snapshot_order_version_restart_and_freshness_have_explicit_states()
    {
        var now = _host.Now;
        var d = new StatusDevice("gpu", "name", null, null, new([new("pstates20", "core", "0")], now), true, true, null, null, false,
            new(now, [], [], DailyComparison.Matched, null), null, []);
        var cache = new TrayStatusCache();
        Assert.True(cache.Accept(new(1, "first", 2, now, [d])));
        Assert.False(cache.Accept(new(1, "first", 1, now, [d with { TrialChange = "old" }])));
        Assert.Equal(TrayState.DailyMatched, TrayStatus.State(d, true, now, now));
        Assert.Equal(TrayState.Unknown, TrayStatus.State(d with { SavedDaily = null }, true, now, now));
        Assert.Equal(TrayState.TrialActive, TrayStatus.State(d with { TrialChange = "trial" }, true, now, now));
        Assert.Equal(TrayState.DailySuspended, TrayStatus.State(d with { SavedDaily = new([], now, Suspended: true) }, true, now, now));
        Assert.Equal(TrayState.Stale, TrayStatus.State(d, true, now, now.AddSeconds(31)));
        Assert.Equal(TrayState.Unavailable, TrayStatus.State(d, false, now, now));
        Assert.True(cache.Accept(new(1, "restarted", 1, now, [d with { Verification = null }])));
        Assert.Null(cache.Snapshot!.Devices[0].Verification);
        Assert.False(cache.Accept(new(4, "future", 1, now, [])));
        Assert.False(cache.Connected);
        Assert.False(cache.Accept(new(1, "malformed", 1, now, null!)));
        Assert.False(cache.Connected);
    }
}
