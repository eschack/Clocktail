using System.Text.Json;
using Clocktail.Domain.Broker;

namespace Clocktail.Tests.Domain;

public sealed class TrayRefreshControllerTests
{
    private sealed class Source(TrayTestClock clock) : ITrayStatusSource
    {
        public readonly List<(StatusFetchKind Kind, string? Gpu)> Calls = [];
        public readonly Dictionary<string, (long Generation, StatusVerification Proof)> Proofs = [];
        public readonly Dictionary<string, StatusControls> Controls = [];
        public RestorationState Startup = RestorationState.Ready, Recovery;
        public long Generation = 1, Revision;
        public bool Settled = true, Measurement, Disconnected, Legacy, NoDevices;
        public string Instance = "broker-A";
        public Func<StatusFetchKind, CancellationToken, Task>? Wait;
        public async Task<TrayStatus?> Fetch(StatusFetchKind kind, string? gpuId, CancellationToken token)
        {
            Calls.Add((kind, gpuId));
            if (Disconnected) return null;
            var response = new TrayStatus(Legacy ? 1 : 2, Instance, ++Revision, clock.Utc,
                (NoDevices ? [] : gpuId == null ? new[] { "A", "B" } : [gpuId]).Select(id => Device(id, kind)).ToArray(),
                ["light-status", "full-verification", "restoration-state", "mutation-generation"]);
            if (Wait != null) await Wait(kind, token);
            return response;
        }
        private StatusDevice Device(string id, StatusFetchKind kind)
        {
            StatusSetting[] settings = [new("pstates20", "core", "kHz", "0")];
            if (kind != StatusFetchKind.Cached) Controls[id] = new(clock.Utc, settings, DailyComparison.Matched, null);
            if (kind == StatusFetchKind.Full) Proofs[id] = (Generation, new(clock.Utc, settings, [], DailyComparison.Matched, null));
            Proofs.TryGetValue(id, out var full);
            return new(id, "GPU " + id, null, null, new([new("pstates20", "core", "0")], clock.Utc), true, true,
                null, null, false, full.Proof, null, [], Legacy ? null : new(Generation, full.Proof == null ? null : full.Generation,
                    new(Startup, Recovery, Generation, null), Settled, Measurement, Controls.GetValueOrDefault(id),
                    full.Proof == null || full.Generation != Generation ? "Full curve verification required" : null));
        }
        public int FullCount => Calls.Count(c => c.Kind == StatusFetchKind.Full);
    }
    [Fact]
    public async Task Startup_waits_for_completed_restoration_then_only_light_ticks_keep_the_true_curve_age()
    {
        var clock = new TrayTestClock(); var source = new Source(clock) { Startup = RestorationState.Pending };
        using var scheduler = new TrayRefreshController(source, clock);
        await scheduler.Tick(); Assert.Equal(0, source.FullCount);
        Assert.Equal(TrayState.StartupPending, TrayStatus.State(scheduler.Device, true, clock.Utc, clock.Utc));
        source.Startup = RestorationState.Ready;
        clock.Advance(15); await scheduler.Tick(); Assert.Equal(1, source.FullCount);
        var fullAt = scheduler.Device!.Verification!.At;
        for (int i = 0; i < 16; i++) { clock.Advance(15); await scheduler.Tick(); }
        Assert.Equal(1, source.FullCount);
        Assert.Equal(fullAt, scheduler.Device!.Verification!.At);
        Assert.Equal(clock.Utc, scheduler.Device.Refresh!.Controls!.At);
        Assert.Equal(TrayState.DailyMatched, TrayStatus.State(scheduler.Device, true, clock.Utc, clock.Utc));
        Assert.All(source.Calls.Where(c => c.Kind != StatusFetchKind.Cached), c => Assert.Equal("A", c.Gpu));
    }
    [Fact]
    public async Task Mutation_bursts_and_recovery_each_coalesce_to_one_full_after_the_guard_releases()
    {
        var clock = new TrayTestClock(); var source = new Source(clock);
        using var scheduler = new TrayRefreshController(source, clock); await scheduler.Tick();
        source.Generation++; source.Settled = false;
        clock.Advance(15); await scheduler.Tick(); Assert.Equal(1, source.FullCount);
        source.Generation += 3;
        clock.Advance(15); await scheduler.Tick(); Assert.Equal(StatusFetchKind.Cached, source.Calls.Last().Kind);
        source.Settled = true;
        clock.Advance(30); await scheduler.Tick(); Assert.Equal(2, source.FullCount);
        source.Generation++; source.Recovery = RestorationState.Pending;
        clock.Advance(15); await scheduler.Tick(); Assert.Equal(2, source.FullCount);
        source.Recovery = RestorationState.Ready; source.Generation++;
        clock.Advance(15); await scheduler.Tick(); Assert.Equal(3, source.FullCount);
        clock.Advance(15); await scheduler.Tick(); Assert.Equal(3, source.FullCount);
    }
    [Fact]
    public async Task Quiet_before_start_stops_all_requests_and_exit_catches_up_once_after_missed_ticks()
    {
        var clock = new TrayTestClock(); var source = new Source(clock);
        using var scheduler = new TrayRefreshController(source, clock);
        scheduler.SetQuiet(true); scheduler.CheckNow(); clock.Advance(3600); await scheduler.Tick();
        Assert.Empty(source.Calls); Assert.Null(scheduler.Delay);
        scheduler.SetQuiet(false); await scheduler.Tick(); Assert.Equal(1, source.FullCount);
        var at = scheduler.Device!.Verification!.At; int count = source.Calls.Count;
        scheduler.SetQuiet(true); source.Generation++; clock.Advance(3600); await scheduler.Tick();
        Assert.Equal(count, source.Calls.Count); Assert.Equal(at, scheduler.Device.Verification.At);
        scheduler.SetQuiet(false); await scheduler.Tick(); Assert.Equal(2, source.FullCount);
        await scheduler.Tick(); Assert.Equal(2, source.FullCount);
    }
    [Fact]
    public async Task Entering_quiet_during_a_sent_request_allows_it_to_finish_but_prevents_followups()
    {
        var clock = new TrayTestClock(); var source = new Source(clock);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Wait = (_, _) => gate.Task;
        using var scheduler = new TrayRefreshController(source, clock);
        var pending = scheduler.Tick(); Assert.True(scheduler.Busy);
        scheduler.SetQuiet(true); gate.SetResult(); await pending;
        Assert.Single(source.Calls); Assert.Equal(0, source.FullCount); Assert.Null(scheduler.Delay);
        source.Wait = null; scheduler.SetQuiet(false); await scheduler.Tick(); Assert.Equal(1, source.FullCount);
    }
    [Fact]
    public async Task Repeated_check_clicks_during_full_read_request_at_most_one_full_followup()
    {
        var clock = new TrayTestClock(); var source = new Source(clock);
        using var scheduler = new TrayRefreshController(source, clock); await scheduler.Tick();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Wait = (_, _) => gate.Task;
        scheduler.CheckNow(); var pending = scheduler.Tick();
        scheduler.CheckNow(); scheduler.CheckNow(); await scheduler.Tick();
        Assert.Equal(2, source.FullCount);
        gate.SetResult(); await pending; source.Wait = null;
        await scheduler.Tick(); Assert.Equal(3, source.FullCount);
        await scheduler.Tick(); Assert.Equal(3, source.FullCount);
    }
    [Fact]
    public async Task Invalidation_is_published_before_a_slow_followup_can_leave_the_old_green_icon_visible()
    {
        var clock = new TrayTestClock(); var source = new Source(clock);
        using var scheduler = new TrayRefreshController(source, clock); await scheduler.Tick();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Wait = (kind, _) => kind == StatusFetchKind.Full ? gate.Task : Task.CompletedTask;
        var observed = new List<TrayState>();
        scheduler.Changed += () => observed.Add(TrayStatus.State(scheduler.Device, scheduler.Cache.Connected, scheduler.Cache.Snapshot!.CapturedAt, clock.Utc));
        source.Generation++; clock.Advance(15); var pending = scheduler.Tick();
        Assert.True(scheduler.Busy); Assert.Contains(TrayState.VerificationPending, observed);
        Assert.Equal(TrayState.VerificationPending, observed.Last());
        gate.SetResult(); await pending; Assert.Equal(TrayState.DailyMatched, observed.Last());
    }
    [Fact]
    public async Task Lock_resume_and_clock_jumps_use_monotonic_schedule_without_replaying_ticks()
    {
        var clock = new TrayTestClock(); var source = new Source(clock);
        using var scheduler = new TrayRefreshController(source, clock); await scheduler.Tick();
        int count = source.Calls.Count;
        clock.Utc = clock.Utc.AddYears(1); await scheduler.Tick(); Assert.Equal(count, source.Calls.Count);
        scheduler.SetLocked(true); scheduler.CheckNow(); clock.Advance(3600); await scheduler.Tick();
        Assert.Equal(count, source.Calls.Count);
        scheduler.SetLocked(false); await scheduler.Tick(); Assert.Equal(2, source.FullCount);
        scheduler.Resume(); await scheduler.Tick(); Assert.Equal(3, source.FullCount);
        clock.Advance(3600); await scheduler.Tick(); await scheduler.Tick(); Assert.Equal(3, source.FullCount);
    }
    [Fact]
    public async Task Measurement_uses_cached_only_deferred_probes_and_retains_pending_manual_full()
    {
        var clock = new TrayTestClock(); var source = new Source(clock) { Measurement = true };
        using var scheduler = new TrayRefreshController(source, clock); scheduler.CheckNow(); await scheduler.Tick();
        Assert.Equal(TimeSpan.FromSeconds(15), scheduler.Delay);
        clock.Advance(15); await scheduler.Tick(); Assert.Equal(TimeSpan.FromSeconds(30), scheduler.Delay);
        clock.Advance(30); await scheduler.Tick(); Assert.Equal(TimeSpan.FromSeconds(60), scheduler.Delay);
        Assert.All(source.Calls, c => Assert.Equal(StatusFetchKind.Cached, c.Kind));
        source.Measurement = false; clock.Advance(60); await scheduler.Tick();
        Assert.Equal(1, source.FullCount); Assert.Equal(TimeSpan.FromSeconds(15), scheduler.Delay);
    }
    [Fact]
    public async Task Reconnect_has_bounded_backoff_and_new_instance_requires_new_proof()
    {
        var clock = new TrayTestClock(); var source = new Source(clock) { Disconnected = true };
        using var scheduler = new TrayRefreshController(source, clock);
        foreach (int delay in new[] { 15, 30, 60, 60 })
        {
            await scheduler.Tick(); Assert.Equal(TimeSpan.FromSeconds(delay), scheduler.Delay); clock.Advance(delay);
        }
        source.Disconnected = false; await scheduler.Tick(); Assert.Equal(1, source.FullCount);
        var oldAt = scheduler.Device!.Verification!.At;
        source.Instance = "broker-B"; source.Revision = 0; source.Proofs.Clear();
        clock.Advance(60); await scheduler.Tick();
        Assert.Equal(2, source.FullCount); Assert.NotEqual(oldAt, scheduler.Device!.Verification!.At);
        Assert.Equal("broker-B", scheduler.Cache.Snapshot!.InstanceId);
    }
    [Fact]
    public async Task Empty_inventory_uses_bounded_discovery_instead_of_a_zero_delay_wakeup_loop()
    {
        var clock = new TrayTestClock(); var source = new Source(clock) { NoDevices = true };
        using var scheduler = new TrayRefreshController(source, clock); await scheduler.Tick();
        Assert.Null(scheduler.GpuId); Assert.Equal(TimeSpan.FromSeconds(15), scheduler.Delay);
        await scheduler.Tick(); Assert.Single(source.Calls);
        clock.Advance(15); await scheduler.Tick(); Assert.Equal(TimeSpan.FromSeconds(30), scheduler.Delay);
        source.NoDevices = false; clock.Advance(30); await scheduler.Tick();
        Assert.Equal("A", scheduler.GpuId); Assert.Equal(1, source.FullCount); Assert.Equal(TimeSpan.FromSeconds(15), scheduler.Delay);
    }
    [Fact]
    public async Task Device_selection_discards_inflight_old_reply_and_never_copies_another_GPUs_proof()
    {
        var clock = new TrayTestClock(); var source = new Source(clock);
        using var scheduler = new TrayRefreshController(source, clock); await scheduler.Tick();
        var firstAt = scheduler.Device!.Verification!.At;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Wait = (_, _) => gate.Task; clock.Advance(15);
        var pending = scheduler.Tick(); scheduler.Select("B"); gate.SetResult(); await pending;
        Assert.False(scheduler.Cache.Connected);
        source.Wait = null; await scheduler.Tick();
        Assert.Equal("B", scheduler.Device!.StableId); Assert.NotEqual(firstAt, scheduler.Device.Verification!.At);
        Assert.Equal(firstAt, scheduler.Cache.Snapshot!.Devices.Single(d => d.StableId == "A").Verification!.At);
        Assert.Equal("B", source.Calls.Last().Gpu);
    }
    [Fact]
    public async Task Legacy_is_manual_only_including_selection_and_disposal_cancels_inflight_work()
    {
        var clock = new TrayTestClock(); var source = new Source(clock) { Legacy = true };
        using var scheduler = new TrayRefreshController(source, clock); await scheduler.Tick();
        clock.Advance(3600); await scheduler.Tick(); Assert.Single(source.Calls); Assert.Null(scheduler.Delay);
        scheduler.Select("B"); await scheduler.Tick(); Assert.Equal(0, source.FullCount);
        scheduler.CheckNow(); await scheduler.Tick(); Assert.Equal(1, source.FullCount); Assert.Equal("B", source.Calls.Last().Gpu);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Wait = async (_, token) => { waiting.SetResult(); await Task.Delay(Timeout.Infinite, token); };
        scheduler.CheckNow(); var pending = scheduler.Tick(); await waiting.Task;
        scheduler.Dispose(); await pending.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Null(scheduler.Delay);
    }
    [Fact]
    public async Task Older_generation_revision_or_retired_instance_cannot_restore_match()
    {
        var clock = new TrayTestClock(); var source = new Source(clock);
        var first = (await source.Fetch(StatusFetchKind.Full, "A", default))!;
        var cache = new TrayStatusCache(); Assert.True(cache.Accept(first));
        source.Generation++; var invalidated = (await source.Fetch(StatusFetchKind.Cached, null, default))!;
        Assert.True(cache.Accept(invalidated)); Assert.False(cache.Accept(first with { Revision = 999 }));
        Assert.Equal(TrayState.VerificationPending, TrayStatus.State(cache.Snapshot!.Devices[0], true, clock.Utc, clock.Utc));
        Assert.True(cache.Accept(invalidated with { InstanceId = "replacement", Revision = 1 }));
        Assert.False(cache.Accept(first with { Revision = 1000 }));
        Assert.Equal("replacement", cache.Snapshot!.InstanceId);
    }
    [Fact]
    public async Task Old_broker_version_fallback_never_sends_light_requests_or_silently_reinterprets_verification()
    {
        var calls = new List<BrokerRequest>(); var clock = new TrayTestClock(); var fake = new Source(clock) { Legacy = true };
        var source = new BrokerTrayStatusSource(async (request, token) =>
        {
            calls.Add(request);
            if (request.Value == "2") return new(false, Error: "unsupported status version", Code: BrokerCode.BadRequest);
            return new(true, Result: JsonSerializer.SerializeToElement(await fake.Fetch(request.Kind == "verify" ? StatusFetchKind.Full : StatusFetchKind.Cached, request.GpuId, token)));
        });
        using var scheduler = new TrayRefreshController(source, clock); await scheduler.Tick();
        Assert.Equal(new[] { "3", "2", "1" }, calls.Select(r => r.Value)); Assert.Contains("Older broker", scheduler.Notice);
        clock.Advance(3600); await scheduler.Tick(); Assert.Equal(3, calls.Count);
        scheduler.CheckNow(); await scheduler.Tick();
        Assert.Equal("verify", calls.Last().Kind); Assert.Equal("1", calls.Last().Value);
        Assert.DoesNotContain(calls, r => r.Kind == "light");
    }
    [Fact]
    public async Task Legacy_manual_failure_does_not_start_background_reconnects()
    {
        var clock = new TrayTestClock(); var source = new Source(clock) { Legacy = true };
        using var scheduler = new TrayRefreshController(source, clock); await scheduler.Tick();
        source.Disconnected = true; scheduler.CheckNow(); await scheduler.Tick();
        int calls = source.Calls.Count; clock.Advance(3600); await scheduler.Tick();
        Assert.Equal(calls, source.Calls.Count); Assert.Null(scheduler.Delay);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_restoration_is_not_automatically_retried_and_manual_diagnostics_do_not_clear_failure(bool startup)
    {
        var clock = new TrayTestClock(); var source = new Source(clock);
        if (startup) source.Startup = RestorationState.Failed; else source.Recovery = RestorationState.Failed;
        using var scheduler = new TrayRefreshController(source, clock); await scheduler.Tick();
        Assert.Equal(0, source.FullCount); scheduler.CheckNow(); await scheduler.Tick(); Assert.Equal(1, source.FullCount);
        Assert.Equal(TrayState.Failed, TrayStatus.State(scheduler.Device, true, clock.Utc, clock.Utc));
    }
    [Fact]
    public async Task Malformed_readiness_generation_comparison_and_duplicate_GPU_ids_are_rejected()
    {
        var clock = new TrayTestClock(); var source = new Source(clock);
        var snapshot = (await source.Fetch(StatusFetchKind.Full, "A", default))!;
        var d = snapshot.Devices[0]; var r = d.Refresh!;
        StatusDevice[] malformed = [d with { Refresh = null }, d with { Refresh = r with { VerifiedGeneration = r.Generation + 1 } },
            d with { Refresh = r with { Restoration = r.Restoration with { Startup = (RestorationState)99 } } },
            d with { Refresh = r with { Controls = r.Controls! with { Comparison = (DailyComparison)99 } } }];
        foreach (var bad in malformed) Assert.False(new TrayStatusCache().Accept(snapshot with { Devices = [bad] }));
        Assert.False(new TrayStatusCache().Accept(snapshot with { Devices = [d, d with { StableId = "a" }] }));
        var incomplete = new BrokerTrayStatusSource((_, _) => Task.FromResult<BrokerResponse?>(new(true,
            Result: JsonSerializer.SerializeToElement(snapshot with { Capabilities = ["light-status", "full-verification"] }))));
        await Assert.ThrowsAsync<InvalidDataException>(() => incomplete.Fetch(StatusFetchKind.Cached, null, default));
    }
    [Fact]
    public async Task Protocol_two_fallback_keeps_automatic_refresh_with_an_explicit_fixed_table_notice()
    {
        var clock = new TrayTestClock(); var fake = new Source(clock); var calls = new List<BrokerRequest>();
        var source = new BrokerTrayStatusSource(async (request, token) =>
        {
            calls.Add(request);
            var kind = request.Kind == "cached" ? StatusFetchKind.Cached : request.Kind == "light" ? StatusFetchKind.Light : StatusFetchKind.Full;
            return new(true, Result: JsonSerializer.SerializeToElement(await fake.Fetch(kind, request.GpuId, token)));
        });
        using var scheduler = new TrayRefreshController(source, clock); await scheduler.Tick();
        Assert.Equal(new[] { "3", "2" }, calls.Take(2).Select(r => r.Value)); Assert.False(scheduler.Legacy);
        Assert.Contains("fixed", scheduler.Notice); clock.Advance(15); await scheduler.Tick();
        Assert.Contains(calls, r => r.Kind == "light" && r.Value == "2"); Assert.DoesNotContain(calls, r => r.Kind == "rows");
    }
    [Fact]
    public async Task Leaving_quiet_while_a_request_is_inflight_keeps_one_immediate_catchup_due()
    {
        var clock = new TrayTestClock(); var source = new Source(clock);
        using var scheduler = new TrayRefreshController(source, clock); await scheduler.Tick(); clock.Advance(15);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.Wait = (_, _) => gate.Task;
        var pending = scheduler.Tick(); scheduler.SetQuiet(true); scheduler.SetQuiet(false); gate.SetResult(); await pending;
        Assert.Equal(TimeSpan.Zero, scheduler.Delay); int calls = source.Calls.Count; source.Wait = null;
        await scheduler.Tick(); Assert.Equal(calls + 1, source.Calls.Count); Assert.Equal(1, source.FullCount);
    }
}
