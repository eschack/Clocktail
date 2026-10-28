using System.Buffers.Binary;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Power;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

internal sealed class TrayTestClock : TimeProvider
{
    private long _timestamp;
    public DateTimeOffset Utc = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _timestamp;
    public override DateTimeOffset GetUtcNow() => Utc;
    public void Advance(double seconds) { _timestamp += TimeSpan.FromSeconds(seconds).Ticks; Utc += TimeSpan.FromSeconds(seconds); }
}

[Collection("fake-driver")]
public sealed partial class AutomaticTrayStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-auto-status", Guid.NewGuid().ToString("N"));
    private readonly FakeHost _host = new();
    private readonly TrayTestClock _clock = new();
    private readonly Target _target;
    private readonly BrokerDispatcher _dispatcher;
    private readonly FakeVf _vf;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    private sealed class Target(string dir, FakeHost host) : IGpuTarget, IGpuTargets
    {
        public string StableId => "gpu-A";
        public string Name => "Fake GPU";
        public GpuSession Session { get; } = new(1, "fake");
        public OwnershipJournal Journal { get; } = new(Path.Combine(dir, "ownership.json"), host, "gpu-A");
        public ApplySentinel Sentinel { get; } = new(Path.Combine(dir, "apply.lock"));
        public IReadOnlyList<string> Ids => [StableId];
        public IGpuTarget? Resolve(string? id) => id is null or "gpu-A" ? this : null;
    }
    private static void W(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static uint Word(Slot slot) => Slots.Info(slot).Shape!.Value.Word;
    public AutomaticTrayStatusTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
        _host.Now = _clock.Utc;
        _target = new(_dir, _host);
        _vf = new() { GlobalKhz = 240000 };
        var hw = _vf.Install(Slot.ClockClkDomainsGetInfo, Slot.ClockClkDomainsGetControl, Slot.ClientPwrPoliciesGetInfo,
            Slot.ClientPwrPoliciesGetControl, Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus);
        var p0 = FakeDriver.Current(Slot.GetPstates20);
        W(p0, 0xc, 2);
        W(p0, Pstates20Layout.Clock(0, 1), (uint)ClockDomain.Memory);
        W(p0, Pstates20Layout.Clock(0, 1) + 4, 1);
        W(p0, Pstates20Layout.Clock(0, 1) + 8, 1);
        foreach (var raw in new[] { ClockDomainsFixture.Topology(), ClockDomainsFixture.FullControl(), RailsFixture.Info(), RailsFixture.Control(), RailsFixture.Status() })
            FakeDriver.State[BitConverter.ToUInt32(raw)] = raw;
        var powerInfo = NvClient.NewBuffer(Slot.ClientPwrPoliciesGetInfo);
        W(powerInfo, 4, 1); int infoAt = PowerRequestsLayout.InfoEntry(0);
        W(powerInfo, infoAt, 1); W(powerInfo, infoAt + 8, 100000); W(powerInfo, infoAt + 12, 450000); W(powerInfo, infoAt + 16, 600000);
        FakeDriver.State[Word(Slot.ClientPwrPoliciesGetInfo)] = powerInfo;
        FakeDriver.Seed(Slot.ClientPwrPoliciesGetControl);
        var vfQuery = FakeDriver.QueryResponder;
        FakeDriver.QueryResponder = query =>
        {
            uint word = BitConverter.ToUInt32(query);
            if (word == Word(Slot.ClockClkDomainsGetControl))
            {
                var answer = (byte[])FakeDriver.State[word].Clone(); W(answer, 8, BitConverter.ToUInt32(query, 8)); return answer;
            }
            if (word == Word(Slot.ClientPwrPoliciesGetControl)) { var answer = (byte[])query.Clone(); W(answer, PowerRequestsLayout.ControlEntry(0), 450000); return answer; }
            return vfQuery?.Invoke(query);
        };
        _dispatcher = new(hw, _target, _host, false) { StatusClock = _clock };
        new DailyProfile([new("pstates20", "core", "240000")], _host.Now).Save(DailyProfile.PathBeside(_target.Journal.Path));
        Ready();
    }
    private void Advance(double seconds) { _clock.Advance(seconds); _host.Now = _clock.Utc; }
    private void Ready()
    {
        _dispatcher.StatusLifecycle.InitializeStartup(["gpu-A"]);
        using (var startup = _dispatcher.StatusLifecycle.Begin("gpu-A", RestorationKind.Startup)) startup.Complete(true);
        Advance(3);
    }
    private TrayStatus Status(string kind, string version = "2")
    {
        var reply = _dispatcher.FromClient(new(BrokerRequest.OpStatus, GpuId: "gpu-A", Kind: kind, Value: version));
        Assert.True(reply.Ok, reply.Error);
        return reply.Result!.Value.Deserialize<TrayStatus>()!;
    }
    private void NoVf() => Assert.DoesNotContain(FakeDriver.GetWords, w => w == FakeVf.InfoWord || w == FakeVf.StatusWord || w == FakeVf.ControlWord);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Light_is_getter_only_has_zero_VF_calls_and_deduplicates_common_tables()
    {
        var light = Status("light");
        NoVf(); Assert.Equal(0, FakeDriver.Sets);
        Assert.Equal(1, FakeDriver.GetWords.Count(w => w == FakeVf.PstatesWord));
        Assert.Equal(1, FakeDriver.GetWords.Count(w => w == Word(Slot.ClockClkDomainsGetControl)));
        Assert.Equal(1, FakeDriver.GetWords.Count(w => w == Word(Slot.VoltVoltRailsGetControl)));
        Assert.Equal(DailyComparison.Matched, light.Devices[0].Refresh!.Controls!.Comparison);
        Assert.Null(light.Devices[0].Verification);
    }
    [Fact]
    public void Cached_remains_driver_free_and_does_not_modify_profile_or_sentinel()
    {
        File.WriteAllText(_target.Sentinel.Path, "an unfinished write");
        var before = Directory.GetFiles(_dir).ToDictionary(p => p, File.ReadAllBytes);
        Status("cached"); Assert.Empty(FakeDriver.GetWords); Assert.Equal(0, FakeDriver.Sets);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    [Fact]
    public void Light_refresh_retains_true_full_timestamp_and_maintains_separate_current_confidence()
    {
        var full = Status("verify").Devices[0];
        Assert.Equal(DailyComparison.Matched, full.Verification!.Comparison);
        Assert.Equal(127, full.Verification.Curve.Count);
        Assert.Equal(TrayState.DailyMatched, TrayStatus.State(full, true, _host.Now, _host.Now));
        FakeDriver.GetWords.Clear(); Advance(240);
        var light = Status("light");
        Assert.Equal(full.Verification.At, light.Devices[0].Verification!.At);
        Assert.Equal(_host.Now, light.Devices[0].Refresh!.Controls!.At);
        Assert.Equal(TrayState.DailyMatched, TrayStatus.State(light.Devices[0], true, light.CapturedAt, _host.Now));
        NoVf(); Assert.Equal(0, FakeDriver.Sets);
    }
    [Fact]
    public void Full_implicit_P0_uses_effective_displacement_even_when_point_controls_mirror_it()
    {
        for (int i = 0; i < 127; i++) W(FakeDriver.State[FakeVf.ControlWord], VfLayout.OffsetField(i), 240000);
        _vf.Effective = (i, _) => _vf.Base(i) + 240000;
        foreach (string version in new[] { "1", "2" }) Assert.Equal(DailyComparison.Matched, Status("verify", version).Devices[0].Verification!.Comparison);
        _vf.Effective = (i, _) => _vf.Base(i) + 480000;
        Assert.Equal(DailyComparison.Unknown, Status("verify").Devices[0].Verification!.Comparison);
        Assert.Equal(0, FakeDriver.Sets);
    }
    [Theory]
    [InlineData("missing common")]
    [InlineData("missing curve")]
    [InlineData("partial mask")]
    public void Full_never_confirms_partial_control_or_effective_curve_reads(string fault)
    {
        if (fault == "missing common") FakeDriver.FailNextGets = 1;
        else if (fault == "missing curve") FakeDriver.State.Remove(FakeVf.StatusWord);
        else _vf.StatusHook = (raw, _) => raw[VfLayout.MaskOffset] ^= 1;
        var full = Status("verify").Devices[0];
        Assert.Equal(DailyComparison.Unknown, full.Verification!.Comparison);
        Assert.NotNull(full.Verification.Error);
        Assert.NotEqual(TrayState.DailyMatched, TrayStatus.State(full, true, _host.Now, _host.Now));
        Assert.Equal(0, FakeDriver.Sets);
    }
    [Fact]
    public void Full_compares_the_explicit_saved_curve_and_all_127_effective_points()
    {
        var profilePath = DailyProfile.PathBeside(_target.Journal.Path);
        (DailyProfile.Load(profilePath)! with { Writes = [new("pstates20", "core", "240000"), new("vf", "core", "0*127")] }).Save(profilePath);
        var full = Status("verify").Devices[0];
        Assert.Equal(DailyComparison.Matched, full.Verification!.Comparison);
        Assert.Equal(127, full.Verification.Curve.Count);
        Assert.Equal(1, FakeDriver.GetWords.Count(w => w == FakeVf.InfoWord));
        Assert.Equal(1, FakeDriver.GetWords.Count(w => w == FakeVf.StatusWord));
        Assert.Equal(1, FakeDriver.GetWords.Count(w => w == FakeVf.ControlWord));
        W(FakeDriver.State[FakeVf.ControlWord], VfLayout.OffsetField(126), 15000);
        var different = Status("verify").Devices[0];
        Assert.Equal(DailyComparison.Different, different.Verification!.Comparison);
        Assert.Equal(TrayState.SettingsDiffer, TrayStatus.State(different, true, _host.Now, _host.Now));
        Assert.Equal(0, FakeDriver.Sets);
    }
    [Fact]
    public void A_lifecycle_invalidation_during_getters_cannot_return_applicable_full_proof()
    {
        _vf.AfterControlRead = _ => _dispatcher.StatusLifecycle.PendingRecovery(["gpu-A"], "synthetic event during read");
        var full = Status("verify").Devices[0];
        Assert.Null(full.Refresh!.Controls);
        Assert.NotEqual(full.Refresh.Generation, full.Refresh.VerifiedGeneration);
        Assert.Equal(RestorationState.Pending, full.Refresh.Restoration.Recovery);
        Assert.Equal(TrayState.Recovering, TrayStatus.State(full, true, _host.Now, _host.Now));
        Assert.Equal(0, FakeDriver.Sets);
    }
    [Fact]
    public async Task Version_two_status_returns_busy_without_waiting_or_reading_the_GPU()
    {
        var operation = typeof(BrokerDispatcher).GetField("_operation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_dispatcher)!;
        using var held = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => { lock (operation) { held.Set(); release.Wait(TimeSpan.FromSeconds(5)); } });
        Assert.True(held.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            foreach (var kind in new[] { "cached", "light", "verify" })
            {
                var during = Status(kind).Devices[0];
                Assert.True(during.OperationInProgress); Assert.Null(during.Refresh!.Controls);
            }
            Assert.Empty(FakeDriver.GetWords); Assert.Equal(0, FakeDriver.Sets);
        }
        finally { release.Set(); await holder; }
    }
    [Fact]
    public void Fake_dispatch_latency_and_native_call_counts_are_recorded_separately_for_each_path()
    {
        var observations = new List<object>();
        foreach (var kind in new[] { "cached", "light", "verify" })
        {
            Status(kind); // warm-up, excluded from the sample distribution
            var elapsed = new List<double>(); var counts = new List<int>();
            for (int i = 0; i < 30; i++)
            {
                FakeDriver.GetWords.Clear(); var watch = System.Diagnostics.Stopwatch.StartNew();
                Status(kind); elapsed.Add(watch.Elapsed.TotalMilliseconds); counts.Add(FakeDriver.GetWords.Count);
                if (kind != "verify") NoVf();
            }
            elapsed.Sort();
            observations.Add(new { kind, samples = elapsed.Count, min_ms = elapsed[0], median_ms = elapsed[15], p95_ms = elapsed[28], max_ms = elapsed[^1], min_getters = counts.Min(), max_getters = counts.Max(), setters = FakeDriver.Sets });
        }
        Assert.Equal(0, FakeDriver.Sets);
        _output.WriteLine("FAKE_DISPATCH_ONLY " + JsonSerializer.Serialize(observations));
    }
    [Theory]
    [InlineData("light")]
    [InlineData("verify")]
    public void Measurement_guards_block_all_extra_getters(string kind)
    {
        _dispatcher.StatusMeasurementReason = () => "fake measurement";
        var device = Status(kind).Devices[0];
        Assert.True(device.Refresh!.MeasurementBusy);
        Assert.Empty(FakeDriver.GetWords); Assert.Equal(0, FakeDriver.Sets);
    }
    [Fact]
    public async Task Version_two_contract_crosses_an_isolated_broker_pipe_with_zero_driver_calls()
    {
        string pipe = "clocktail-refresh-test-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = new BrokerServer(() => new System.IO.Pipes.NamedPipeServerStream(pipe, System.IO.Pipes.PipeDirection.InOut,
            1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous), _dispatcher);
        var serving = server.ServeOneAsync(stop.Token);
        var source = new BrokerTrayStatusSource((request, token) => BrokerClient.TryCallAsync(request, token, pipe));
        var snapshot = await source.Fetch(StatusFetchKind.Cached, null, stop.Token); await serving;
        Assert.Equal(3, snapshot!.Version); Assert.Contains("restoration-state", snapshot.Capabilities!);
        Assert.Equal(RestorationState.Ready, snapshot.Devices[0].Refresh!.Restoration.Startup);
        Assert.Empty(FakeDriver.GetWords); Assert.Equal(0, FakeDriver.Sets);
    }
    [Fact]
    public void Exhausted_request_budget_stops_subsequent_controls_and_full_curve_reads()
    {
        var query = FakeDriver.QueryResponder;
        FakeDriver.QueryResponder = raw => { Advance(6); return query?.Invoke(raw); };
        var full = Status("verify").Devices[0];
        Assert.Single(FakeDriver.GetWords); NoVf(); Assert.Equal(0, FakeDriver.Sets);
        Assert.Equal(DailyComparison.Unknown, full.Verification!.Comparison);
        Assert.Contains("budget exceeded", full.Verification.Error);
    }
    [Fact]
    public void Mutations_and_external_profile_changes_invalidate_without_redating_old_curve_evidence()
    {
        var initial = Status("verify").Devices[0];
        _dispatcher.FromClient(new(BrokerRequest.OpWrite, Kind: "unsupported", Key: "x", Value: "1"));
        FakeDriver.GetWords.Clear();
        var busy = Status("verify").Devices[0];
        Assert.False(busy.Refresh!.Settled); Assert.Empty(FakeDriver.GetWords);
        Assert.NotEqual(initial.Refresh!.VerifiedGeneration, busy.Refresh.Generation);
        Advance(3);
        var profilePath = DailyProfile.PathBeside(_target.Journal.Path);
        var changed = DailyProfile.Load(profilePath)! with { Writes = [new("pstates20", "core", "0")] };
        changed.Save(profilePath);
        var cached = Status("cached").Devices[0];
        Assert.Equal(initial.Verification!.At, cached.Verification!.At);
        Assert.NotEqual(cached.Refresh!.Generation, cached.Refresh.VerifiedGeneration);
        Assert.Equal(0, FakeDriver.Sets);
    }
    [Fact]
    public void Observed_external_control_change_requires_one_new_full_check()
    {
        var initial = Status("verify").Devices[0];
        W(FakeDriver.Current(Slot.GetPstates20), Pstates20Layout.Clock(0, 0) + 0xc, 0);
        var light = Status("light").Devices[0];
        Assert.True(light.Refresh!.Generation > initial.Refresh!.Generation);
        Assert.Equal(DailyComparison.Different, light.Refresh.Controls!.Comparison);
        Assert.Equal(initial.Verification!.At, light.Verification!.At);
        var full = Status("verify").Devices[0];
        Assert.Equal(full.Refresh!.Generation, full.Refresh.VerifiedGeneration);
        Assert.Null(full.Refresh.FullRequiredReason);
        Assert.Equal(DailyComparison.Different, full.Verification!.Comparison);
    }
    [Fact]
    public void Startup_reset_and_resume_are_pending_until_the_whole_responder_sequence_finishes()
    {
        _dispatcher.StatusLifecycle.InitializeStartup(["gpu-A"]);
        int observed = 0;
        var responder = new DriverResetResponder(request =>
        {
            if (request.Op == BrokerRequest.OpProfile && request.Kind is "apply" or "after-reset" or "resume")
            {
                FakeDriver.GetWords.Clear(); var during = Status("verify").Devices[0];
                Assert.True(during.Refresh!.Restoration.Startup == RestorationState.Pending || during.Refresh.Restoration.Recovery == RestorationState.Pending);
                Assert.Empty(FakeDriver.GetWords); observed++;
            }
            return new(true, Result: JsonSerializer.SerializeToElement(new { profile = (object?)null }));
        }, ["gpu-A"], _ => { }, () => _host.Now, _ => { }, statusLifecycle: _dispatcher.StatusLifecycle);
        responder.ApplyAtStart([]);
        Assert.Equal(RestorationState.Ready, _dispatcher.StatusLifecycle.Read("gpu-A").Startup);
        responder.Signal("synthetic reset");
        Assert.Equal(RestorationState.Pending, _dispatcher.StatusLifecycle.Read("gpu-A").Recovery);
        Assert.True(responder.Respond("synthetic reset"));
        Assert.Equal(RestorationState.Ready, _dispatcher.StatusLifecycle.Read("gpu-A").Recovery);
        responder.AfterSleep("synthetic resume");
        Assert.Equal(3, observed);
        Assert.Equal(RestorationState.Ready, _dispatcher.StatusLifecycle.Read("gpu-A").Recovery);
        Assert.Equal(0, FakeDriver.Sets);
    }
    [Fact]
    public void Overlapping_startup_cannot_clear_a_queued_reset_and_failed_recovery_stays_failed()
    {
        var lifecycle = new BrokerStatusLifecycle(); lifecycle.InitializeStartup(["gpu"]);
        using var startup = lifecycle.Begin("gpu", RestorationKind.Startup);
        lifecycle.PendingRecovery(["gpu"], "queued reset"); startup.Complete(true);
        Assert.Equal(RestorationState.Pending, lifecycle.Read("gpu").Recovery);
        using (var reset = lifecycle.Begin("gpu", RestorationKind.DriverReset)) reset.Complete(false);
        Assert.Equal(RestorationState.Failed, lifecycle.Read("gpu").Recovery);
    }
}
