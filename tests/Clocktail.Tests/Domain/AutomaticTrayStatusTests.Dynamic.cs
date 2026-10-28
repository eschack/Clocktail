using System.Buffers.Binary;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Power;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Writes;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

public sealed partial class AutomaticTrayStatusTests
{
    private Clocktail.Tests.Hw.FakeApi? _dynamicApi;
    private BrokerDispatcher DynamicDispatcher()
    {
        _dynamicApi = FakeDriver.ApiFor(Slots.All.Where(s => s.Access == SlotAccess.Read && s.Signature == Signature.GpuStruct).Select(s => s.Slot));
        var d = new BrokerDispatcher(new(_dynamicApi), _target, _host, false) { StatusClock = _clock };
        d.StatusLifecycle.InitializeStartup(["gpu-A"]);
        using (var startup = d.StatusLifecycle.Begin("gpu-A", RestorationKind.Startup)) startup.Complete(true);
        Advance(3); return d;
    }
    private TrayStatus Dynamic(BrokerDispatcher d, string kind)
    {
        var reply = d.FromClient(new(BrokerRequest.OpStatus, GpuId: "gpu-A", Kind: kind, Value: "3"));
        Assert.True(reply.Ok, reply.Error); var result = reply.Result!.Value.Deserialize<TrayStatus>()!;
        Assert.True(new TrayStatusCache().Accept(result)); return result;
    }
    private void SaveDynamic(params ProfileWrite[] writes) => new DailyProfile(writes, _host.Now).Save(DailyProfile.PathBeside(_target.Journal.Path));
    private void ExtraControls()
    {
        CurrentLimitsFixture.Install();
        foreach (var b in new[] { FanPolicyFixture.Info(), FanPolicyFixture.Control() }) FakeDriver.State[BitConverter.ToUInt32(b)] = b;
        var info = NvClient.NewBuffer(Slot.ClientFanCoolersGetInfo); W(info, FanLayout.InfoCount, 1); W(info, FanLayout.InfoEntry(0), 37);
        var status = NvClient.NewBuffer(Slot.ClientFanCoolersGetStatus); W(status, FanLayout.StatusCount, 1); W(status, FanLayout.StatusEntry(0), 37);
        W(status, FanLayout.StatusEntry(0) + FanLayout.StatusMax, 100);
        var control = NvClient.NewBuffer(Slot.ClientFanCoolersGetControl); W(control, FanLayout.ControlCount, 1); W(control, FanLayout.ControlEntry(0), 37);
        W(control, FanLayout.ControlEntry(0) + FanLayout.ControlMode, 1); W(control, FanLayout.ControlEntry(0) + FanLayout.ControlLevel, 50);
        foreach (var b in new[] { info, status, control }) FakeDriver.State[BitConverter.ToUInt32(b)] = b;
        var ratioInfo = NvClient.NewBuffer(Slot.ClockClkPropTopRelsGetInfo); W(ratioInfo, ClockRatio.InfoRelations, 1);
        var ratioControl = NvClient.NewBuffer(Slot.ClockClkPropTopRelsGetControl); W(ratioControl, ClockRatio.RatioAt, 58976);
        FakeDriver.State[BitConverter.ToUInt32(ratioInfo)] = ratioInfo; FakeDriver.State[BitConverter.ToUInt32(ratioControl)] = ratioControl;
        var limits = NvClient.NewBuffer(Slot.PerfClientLimitsGetStatus);
        W(limits, 8, 7);
        for (int i = 0; i < 7; i++) W(limits, ClientLimitsLayout.First + i * ClientLimitsLayout.Stride, (uint)i);
        W(limits, ClientLimitsLayout.First + ClientLimitsLayout.Stride + 8, 2); W(limits, ClientLimitsLayout.First + ClientLimitsLayout.Stride + 16, 1500000);
        FakeDriver.State[BitConverter.ToUInt32(limits)] = limits;
        var query = FakeDriver.QueryResponder;
        FakeDriver.QueryResponder = raw =>
        {
            if (BitConverter.ToUInt32(raw) == Word(Slot.FanPolicyGetControl))
            { var result = (byte[])FakeDriver.State[Word(Slot.FanPolicyGetControl)].Clone(); W(result, 4, BitConverter.ToUInt32(raw, 4)); return result; }
            return query?.Invoke(raw);
        };
    }
    [Fact]
    public void Dynamic_catalog_accounts_for_every_canonical_write_family()
    {
        Assert.Equal(WriteRegistry.All.Select(p => p.Kind).Order(), AppliedCatalog.All.Select(p => p.Kind).Order());
        Assert.All(AppliedCatalog.All, d => { Assert.NotEqual(AppliedType.Unknown, d.Type); Assert.NotEmpty(d.Keys); });
    }
    [Theory]
    [InlineData("pstates20", "core", "-15000", "-15 MHz")]
    [InlineData("clockratio", "msvdd", "58976", "0.8999×")]
    [InlineData("clocklimit", "min", "0", "Off · released")]
    [InlineData("clocklimit", "voltage", "1500000", "Boost Lock · 1500 mV")]
    [InlineData("power", "f8", "absent", "Absent · no request")]
    [InlineData("ocp", "nvvdd", "350000", "350 A")]
    [InlineData("fan", "37", "manual:50", "Manual · level 50")]
    public void Dynamic_values_use_control_types_instead_of_a_universal_MHz_fallback(string kind, string key, string raw, string formatted)
        => Assert.Equal(formatted, AppliedCatalog.Format(AppliedCatalog.Type(kind, key), raw));
    [Fact]
    public void Dynamic_semantic_equality_handles_partial_slots_ratio_aliases_and_modes()
    {
        Assert.True(AppliedCatalog.Equal("rails", "msvdd", "rel=-5000,alt=2000,ov=0,vmin=0", "rel=-5000"));
        Assert.False(AppliedCatalog.Equal("rails", "msvdd", "rel=-5000,alt=2000,ov=0,vmin=0", "alt=0"));
        Assert.True(AppliedCatalog.Equal("clockratio", "msvdd", "65536", "1.0"));
        Assert.True(AppliedCatalog.Equal("clocklimit", "min", "0", "off"));
        Assert.Null(AppliedCatalog.Equal("unknown", "x", "1", "1"));
    }
    [Fact]
    public void Dynamic_saved_zero_unknown_and_unreadable_values_remain_visible_without_any_cached_GPU_reads()
    {
        SaveDynamic(new("pstates20", "core", "0"), new("future", "control", "123"));
        var d = DynamicDispatcher(); var before = Directory.GetFiles(_dir).ToDictionary(p => p, File.ReadAllBytes);
        var cached = Dynamic(d, "cached"); Assert.Empty(FakeDriver.GetWords);
        Assert.Contains(cached.Devices[0].Applied!.Rows, r => r.Saved && r.Kind == "future" && r.SavedValue == "123");
        var full = Dynamic(d, "verify");
        Assert.Contains(full.Devices[0].Applied!.Rows, r => r.Id == "pstates20/core" && r.SavedValue == "0");
        Assert.Contains(full.Devices[0].Applied!.Rows, r => r.Kind == "future" && r.Modification == AppliedModification.Unavailable && r.Error != null);
        Assert.NotEqual(DailyComparison.Matched, full.Devices[0].Verification!.Comparison);
        foreach (var (path, value) in before) Assert.Equal(value, File.ReadAllBytes(path));
        Assert.Equal(0, FakeDriver.Sets);
    }
    [Fact]
    public void Dynamic_unreadable_saved_record_keeps_last_targets_as_unknown_instead_of_not_saved()
    {
        var d = DynamicDispatcher(); var first = Dynamic(d, "verify");
        File.WriteAllText(DailyProfile.PathBeside(_target.Journal.Path), "not valid JSON"); FakeDriver.GetWords.Clear();
        var failed = Dynamic(d, "cached").Devices[0]; Assert.False(failed.SavedDailyKnown);
        Assert.Contains(failed.Applied!.Rows, r => r.Id == "pstates20/core" && r.Saved && r.SavedValue == "240000" && r.Daily == DailyComparison.Unknown && r.Context!.Contains("last known"));
        Assert.Equal(first.Devices[0].Applied!.Rows.Single(r => r.Kind == "vf").SampledAt, failed.Applied.Rows.Single(r => r.Kind == "vf").SampledAt);
        Assert.Empty(FakeDriver.GetWords); Assert.Equal(0, FakeDriver.Sets);
    }
    [Fact]
    public void Dynamic_inventory_discovers_unsaved_video_changes_and_keeps_true_VF_age_on_light_ticks()
    {
        W(FakeDriver.Current(Slot.ClockClkDomainsGetControl), ClockDomainsLayout.OffsetAt(4, 0xf), 100000);
        var d = DynamicDispatcher(); var full = Dynamic(d, "verify"); var rows = full.Devices[0].Applied!.Rows;
        Assert.Contains(rows, r => r.Id == "clockdomain/video" && !r.Saved && r.Modification == AppliedModification.Modified && r.CurrentValue == "100000");
        Assert.DoesNotContain(rows, r => r.Id == "boost/legacy");
        var vf = rows.Single(r => r.Kind == "vf"); FakeDriver.GetWords.Clear(); Advance(240);
        var light = Dynamic(d, "light").Devices[0]; NoVf();
        Assert.Equal(vf.SampledAt, light.Applied!.Rows.Single(r => r.Kind == "vf").SampledAt);
        Assert.Equal(_host.Now, light.Applied.Rows.Single(r => r.Id == "clockdomain/video").SampledAt);
        Assert.Equal(full.Devices[0].Verification!.At, light.Verification!.At);
        Assert.Equal(TrayState.DailyMatched, TrayStatus.State(light, true, _host.Now, _host.Now));
    }
    [Fact]
    public void Dynamic_trial_only_controls_and_structured_policies_use_supported_ids_and_firmware_references()
    {
        ExtraControls(); var d = DynamicDispatcher();
        W(FakeDriver.Current(Slot.PowerPolicyGetControl), PolicyTableLayout.ControlLimit(13), 350000);
        var full = Dynamic(d, "verify").Devices[0];
        Assert.Contains(full.Applied!.Rows, r => r.Id == "fan/37" && !r.DailyEligible && r.CurrentValue == "manual:50");
        Assert.Contains(full.Applied.Rows, r => r.Id == "ocp/nvvdd" && r.Reference == AppliedReference.Firmware && r.ReferenceValue == "600000" && !r.DailyEligible);
        Assert.Contains(full.Applied.Rows, r => r.Id == "clocklimit/min" && !r.DailyEligible && r.CurrentValue == "1500000");
        Assert.DoesNotContain(full.Applied.Rows, r => r.Id == "clockratio/msvdd"); // unknown default is not invented
        Assert.Equal(DailyComparison.Matched, full.Verification!.Comparison); // exploratory missing reads do not poison daily proof
        Assert.Equal(0, FakeDriver.Sets);
    }
    [Fact]
    public void Dynamic_claimed_unchanged_dependencies_hide_and_foreign_boot_originals_are_not_baselines()
    {
        ExtraControls(); _target.Journal.Claim(new("clockratio", "msvdd", "58976", _host.Now));
        var d = DynamicDispatcher(); Assert.DoesNotContain(Dynamic(d, "verify").Devices[0].Applied!.Rows, r => r.Kind == "clockratio");
        W(FakeDriver.Current(Slot.ClockClkPropTopRelsGetControl), ClockRatio.RatioAt, 60000);
        var modified = Dynamic(d, "verify").Devices[0].Applied!.Rows.Single(r => r.Kind == "clockratio");
        Assert.Equal(AppliedReference.CapturedOriginal, modified.Reference); Assert.Equal("58976", modified.ReferenceValue);
        var record = _target.Journal.Load()! with { BootId = "old-boot" };
        File.WriteAllText(_target.Journal.Path, JsonSerializer.Serialize(record));
        var nextBroker = DynamicDispatcher();
        Assert.DoesNotContain(Dynamic(nextBroker, "verify").Devices[0].Applied!.Rows, r => r.Kind == "clockratio");
        Assert.Contains(Dynamic(nextBroker, "cached").Devices[0].Applied!.Coverage, n => n.Contains("ignored"));
    }
    [Fact]
    public void Dynamic_unreadable_modified_rows_keep_last_evidence_and_return_only_after_fresh_baseline_readback()
    {
        var control = FakeDriver.Current(Slot.ClockClkDomainsGetControl); int at = ClockDomainsLayout.OffsetAt(4, 0xf); W(control, at, 100000);
        var d = DynamicDispatcher(); var first = Dynamic(d, "verify").Devices[0].Applied!.Rows.Single(r => r.Id == "clockdomain/video");
        var query = FakeDriver.QueryResponder;
        FakeDriver.QueryResponder = raw => BitConverter.ToUInt32(raw) == Word(Slot.ClockClkDomainsGetControl) && BitConverter.ToUInt32(raw, 8) == 16
            ? new byte[raw.Length] : query?.Invoke(raw);
        Advance(15); var failed = Dynamic(d, "light").Devices[0].Applied!.Rows.Single(r => r.Id == first.Id);
        Assert.Null(failed.CurrentValue); Assert.Equal("100000", failed.LastValue); Assert.Equal(first.SampledAt, failed.SampledAt); Assert.NotNull(failed.Error);
        FakeDriver.QueryResponder = query; W(control, at, 0); Advance(15);
        Assert.DoesNotContain(Dynamic(d, "light").Devices[0].Applied!.Rows, r => r.Id == first.Id);
    }
    [Fact]
    public void Dynamic_light_does_not_rescan_unchanged_inventory_and_new_external_changes_wait_for_full_discovery()
    {
        var d = DynamicDispatcher(); Dynamic(d, "verify");
        W(FakeDriver.Current(Slot.ClockClkDomainsGetControl), ClockDomainsLayout.OffsetAt(4, 0xf), 100000);
        FakeDriver.GetWords.Clear(); Advance(15);
        Assert.DoesNotContain(Dynamic(d, "light").Devices[0].Applied!.Rows, r => r.Id == "clockdomain/video"); NoVf();
        Assert.Contains(Dynamic(d, "verify").Devices[0].Applied!.Rows, r => r.Id == "clockdomain/video");
    }
    [Fact]
    public void Dynamic_profile_more_than_64_candidates_is_explicit_and_never_silently_omits_saved_targets()
    {
        SaveDynamic(Enumerable.Range(0, 64).Select(i => new ProfileWrite("future", "key" + i, "0")).ToArray());
        var d = DynamicDispatcher(); var full = Dynamic(d, "verify").Devices[0];
        Assert.Equal(64, full.Applied!.Rows.Count(r => r.Saved)); Assert.True(full.Refresh!.Controls!.Settings.Count > 64);
        Assert.NotEqual(DailyComparison.Matched, full.Verification!.Comparison);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(full).Length < BrokerTransport.MaxMessageBytes);
    }
    [Fact]
    public void Dynamic_measurement_and_lifecycle_guards_preserve_rows_without_sampling_or_settlement()
    {
        var d = DynamicDispatcher(); var first = Dynamic(d, "verify");
        File.WriteAllText(_target.Sentinel.Path, "unchanged sentinel"); var bytes = File.ReadAllBytes(_target.Sentinel.Path);
        d.StatusMeasurementReason = () => "fake measurement"; FakeDriver.GetWords.Clear(); Advance(15);
        var paused = Dynamic(d, "verify"); Assert.Empty(FakeDriver.GetWords); Assert.Equal(0, FakeDriver.Sets);
        Assert.Equal(first.Devices[0].Applied!.Rows.Single(r => r.Kind == "vf").SampledAt, paused.Devices[0].Applied!.Rows.Single(r => r.Kind == "vf").SampledAt);
        Assert.Equal(bytes, File.ReadAllBytes(_target.Sentinel.Path));
    }
    [Fact]
    public async Task Dynamic_readonly_pages_collect_every_saved_row_without_GPU_calls_and_reject_stale_cursors()
    {
        SaveDynamic(Enumerable.Range(0, 64).Select(i => new ProfileWrite("future", "setting" + i, new string('a', 2048))).ToArray());
        var d = DynamicDispatcher(); var calls = new List<BrokerRequest>();
        var source = new BrokerTrayStatusSource((request, _) => { calls.Add(request); return Task.FromResult<BrokerResponse?>(d.FromClient(request)); });
        var snapshot = await source.Fetch(StatusFetchKind.Cached, null, default);
        Assert.Equal(64, snapshot!.Devices[0].Applied!.Rows.Count(r => r.Saved));
        Assert.All(snapshot.Devices[0].Applied!.Rows, r => Assert.Equal(2048, r.SavedValue!.Length));
        Assert.Contains(calls, r => r.Kind == "rows"); Assert.Empty(FakeDriver.GetWords); Assert.Equal(0, FakeDriver.Sets);
        var raw = d.FromClient(new(BrokerRequest.OpStatus, GpuId: "gpu-A", Kind: "cached", Value: "3"));
        var first = raw.Result!.Value.Deserialize<TrayStatus>()!; string cursor = first.Devices[0].Applied!.NextCursor!;
        Assert.NotNull(cursor); Assert.True(JsonSerializer.SerializeToUtf8Bytes(raw).Length < BrokerTransport.MaxMessageBytes);
        d.FromClient(new(BrokerRequest.OpWrite, Kind: "unsupported", Key: "x", Value: "0"));
        Assert.False(d.FromClient(new(BrokerRequest.OpStatus, GpuId: "gpu-A", Kind: "rows", Key: cursor, Value: "3")).Ok);
        Assert.Empty(FakeDriver.GetWords);
    }
    [Fact]
    public async Task Dynamic_page_context_changes_are_rejected_before_new_status_is_accepted()
    {
        SaveDynamic(Enumerable.Range(0, 64).Select(i => new ProfileWrite("future", "setting" + i, new string('a', 2048))).ToArray());
        var d = DynamicDispatcher();
        var source = new BrokerTrayStatusSource((request, _) =>
        {
            if (request.Kind == "rows") d.FromClient(new(BrokerRequest.OpWrite, Kind: "unsupported", Key: "x", Value: "0"));
            return Task.FromResult<BrokerResponse?>(d.FromClient(request));
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => source.Fetch(StatusFetchKind.Cached, null, default));
        Assert.Empty(FakeDriver.GetWords);
    }
    [Fact]
    public void Dynamic_saved_zero_partial_rail_targets_and_adopted_baselines_have_explicit_presence()
    {
        W(FakeDriver.Current(Slot.GetPstates20), Pstates20Layout.Clock(0, 0) + 0xc, 0); _vf.GlobalKhz = 0;
        var profile = new DailyProfile([new("pstates20", "core", "0"), new("rails", "nvvdd", "rel=0")], _host.Now,
            Adopted: [new("clockratio", "msvdd", "58976")]);
        profile.Save(DailyProfile.PathBeside(_target.Journal.Path)); ExtraControls(); var d = DynamicDispatcher();
        var rows = Dynamic(d, "verify").Devices[0].Applied!.Rows;
        Assert.Contains(rows, r => r.Id == "pstates20/core" && r.Saved && r.CurrentValue == "0" && r.Modification == AppliedModification.Unchanged);
        Assert.Contains(rows, r => r.Id == "rails/nvvdd/rel" && r.Saved && r.SavedValue == "0");
        Assert.DoesNotContain(rows, r => r.Id == "rails/nvvdd/alt" && r.Saved);
        Assert.Contains(rows, r => r.Id == "clockratio/msvdd" && r.Saved && r.Reference == AppliedReference.AdoptedBaseline);
    }
    [Fact]
    public void Dynamic_current_readback_never_comes_from_originals_and_unknown_owned_keys_stay_diagnostic()
    {
        ExtraControls(); _target.Journal.Claim(new("clockratio", "msvdd", "60000", _host.Now));
        _target.Journal.Claim(new("future", "owned", "12345", _host.Now));
        var d = DynamicDispatcher(); var cached = Dynamic(d, "cached").Devices[0].Applied!.Rows;
        Assert.Contains(cached, r => r.Kind == "clockratio" && r.CurrentValue == null && r.LastValue == null);
        var full = Dynamic(d, "verify").Devices[0];
        Assert.Contains(full.Applied!.Rows, r => r.Kind == "clockratio" && r.CurrentValue == "58976" && r.ReferenceValue == "60000");
        Assert.Contains(full.Applied.Rows, r => r.Id == "future/owned" && r.CurrentValue == null && r.Reference == AppliedReference.Unknown && r.Error != null);
        Assert.Equal(DailyComparison.Matched, full.Verification!.Comparison);
    }
    [Fact]
    public void Dynamic_all_adapter_reads_are_getter_only_and_required_tables_are_deduplicated()
    {
        ExtraControls(); FakeDriver.Seed(Slot.ClientVoltRailsGetControl);
        foreach (var w in new[] { new ProfileWrite("fancurve", "0", "50:20:1100,83:50:1775,88:100:2875"), new("fanstop", "0", "43:50"), new("rails", "nvvdd", "rel=0,alt=0,ov=0,vmin=0"), new("clockratio", "msvdd", "60000") })
            _target.Journal.Claim(new(w.Kind, w.Key, w.Value, _host.Now));
        var d = DynamicDispatcher(); var raw = Dynamic(d, "verify");
        Assert.Equal(0, FakeDriver.Sets); Assert.Equal(1, FakeDriver.GetWords.Count(w => w == FakeVf.PstatesWord));
        Assert.Equal(1, FakeDriver.GetWords.Count(w => w == FakeVf.InfoWord)); Assert.Equal(1, FakeDriver.GetWords.Count(w => w == FakeVf.StatusWord));
        Assert.Equal(1, FakeDriver.GetWords.Count(w => w == FakeVf.ControlWord));
        Assert.Equal(2, FakeDriver.GetWords.Count(w => w == Word(Slot.FanPolicyGetControl))); // distinct policies, shared curve/stop reads
        Assert.All(_dynamicApi!.Resolved, s => Assert.Equal(SlotAccess.Read, Slots.Info(s).Access));
        Assert.True(new TrayStatusCache().Accept(raw));
        var invalidGpu = raw with { Devices = [raw.Devices[0] with { Applied = raw.Devices[0].Applied! with { GpuId = "different-gpu" } }] };
        Assert.False(new TrayStatusCache().Accept(invalidGpu));
    }
    [Fact]
    public async Task Dynamic_quiet_stops_followup_pages_after_an_already_sent_root_request()
    {
        SaveDynamic(Enumerable.Range(0, 64).Select(i => new ProfileWrite("future", "setting" + i, new string('a', 2048))).ToArray());
        var d = DynamicDispatcher(); var calls = new List<BrokerRequest>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new BrokerTrayStatusSource(async (request, _) =>
        {
            calls.Add(request); var result = d.FromClient(request);
            if (calls.Count == 1) await gate.Task;
            return result;
        });
        using var scheduler = new TrayRefreshController(source, _clock); var pending = scheduler.Tick();
        scheduler.SetQuiet(true); gate.SetResult(); await pending;
        Assert.Single(calls); Assert.Empty(FakeDriver.GetWords); Assert.Null(scheduler.Delay);
        scheduler.SetQuiet(false); await scheduler.Tick();
        Assert.Contains(calls, r => r.Kind == "rows"); Assert.Single(calls, r => r.Kind == "verify"); Assert.Equal(0, FakeDriver.Sets);
    }
    [Fact]
    public void Dynamic_topology_changes_invalidate_old_captured_references_and_preserve_unconfirmed_rows()
    {
        ExtraControls(); _target.Journal.Claim(new("clockratio", "msvdd", "60000", _host.Now));
        var d = DynamicDispatcher(); var first = Dynamic(d, "verify").Devices[0];
        Assert.Contains(first.Applied!.Rows, r => r.Kind == "clockratio" && r.Reference == AppliedReference.CapturedOriginal);
        Advance(15); var topology = FakeDriver.Current(Slot.ClockClkDomainsGetInfo);
        W(topology, ClockDomainsLayout.TopologyAt(1) + 0x78, 1); topology[ClockDomainsLayout.TopologyAt(1) + 0x80] = 1;
        var next = Dynamic(d, "light").Devices[0];
        Assert.True(next.Refresh!.Generation > first.Refresh!.Generation);
        Assert.NotEqual(next.Refresh.Generation, next.Refresh.VerifiedGeneration);
        Assert.Contains(next.Applied!.Rows, r => r.Kind == "clockratio" && r.Reference == AppliedReference.Unknown && r.Modification == AppliedModification.Unconfirmed);
    }
    [Fact]
    public void Dynamic_future_original_and_oversized_values_have_explicit_unconfirmed_diagnostics()
    {
        ExtraControls(); _target.Journal.Claim(new("clockratio", "msvdd", "60000", _host.Now.AddDays(1)));
        SaveDynamic(new ProfileWrite("future", new string('x', 300), new string('a', 5000)));
        var d = DynamicDispatcher(); var status = Dynamic(d, "verify").Devices[0];
        Assert.Contains(status.Applied!.Rows, r => r.Kind == "clockratio" && r.Reference == AppliedReference.Unknown);
        Assert.Contains(status.Applied.Rows, r => r.Saved && r.SavedValue == null && r.Error!.Contains("identity"));
        Assert.Contains(status.Applied.Coverage, c => c.Contains("value exceeds"));
        Assert.False(status.Applied.Complete); Assert.NotEqual(DailyComparison.Matched, status.Verification!.Comparison);
    }
    [Fact]
    public void Dynamic_discovery_obeys_the_shared_read_budget_and_does_not_attempt_unsupported_native_interfaces()
    {
        var d = DynamicDispatcher(); var query = FakeDriver.QueryResponder;
        FakeDriver.QueryResponder = raw => { Advance(6); return query?.Invoke(raw); };
        var result = Dynamic(d, "verify").Devices[0];
        Assert.Single(FakeDriver.GetWords); NoVf(); Assert.Equal(0, FakeDriver.Sets);
        Assert.False(result.Applied!.Complete); Assert.Contains(result.Applied.Coverage, c => c.Contains("budget"));
    }
    [Fact]
    public void Dynamic_unreadable_unsaved_controls_are_reported_as_partial_coverage_even_without_a_visible_row()
    {
        ExtraControls(); var query = FakeDriver.QueryResponder;
        FakeDriver.QueryResponder = raw => BitConverter.ToUInt32(raw) == Word(Slot.FanPolicyGetControl) ? new byte[raw.Length] : query?.Invoke(raw);
        var d = DynamicDispatcher(); var full = Dynamic(d, "verify").Devices[0];
        Assert.False(full.Applied!.Complete); Assert.Contains(full.Applied.Coverage, c => c.StartsWith("fancurve/"));
        Assert.Equal(DailyComparison.Matched, full.Verification!.Comparison);
    }
    [Fact]
    public void Dynamic_power_uses_firmware_defaults_and_only_accepts_absence_when_the_board_proves_it()
    {
        var info = FakeDriver.Current(Slot.ClientPwrPoliciesGetInfo); W(info, PowerRequestsLayout.InfoEntry(0), 0);
        W(info, PowerRequestsLayout.InfoEntry(0) + 12, 575000);
        var d = DynamicDispatcher(); var present = Dynamic(d, "verify").Devices[0].Applied!.Rows.Single(r => r.Id == "power/fe");
        Assert.Equal("450000", present.CurrentValue); Assert.Equal("575000", present.ReferenceValue); Assert.Equal(AppliedReference.Firmware, present.Reference);
        var query = FakeDriver.QueryResponder;
        FakeDriver.QueryResponder = raw =>
        {
            if (BitConverter.ToUInt32(raw) == Word(Slot.ClientPwrPoliciesGetControl) && BitConverter.ToUInt32(raw, PowerRequestsLayout.ControlEntry(0) + 4) == (uint)PowerRequestKind.Ordinary)
            { var answer = (byte[])raw.Clone(); W(answer, PowerRequestsLayout.ControlEntry(0), 0); return answer; }
            return query?.Invoke(raw);
        };
        var uncertain = Dynamic(d, "verify").Devices[0].Applied!.Rows.Single(r => r.Id == "power/fe");
        Assert.Null(uncertain.CurrentValue); Assert.Equal("450000", uncertain.LastValue); Assert.NotNull(uncertain.Error);
        W(info, PowerRequestsLayout.InfoEntry(0), 1);
        Assert.DoesNotContain(Dynamic(d, "verify").Devices[0].Applied!.Rows, r => r.Id == "power/fe");
        Assert.False(AppliedCatalog.Equal("power", "fe", "absent", "575000"));
    }
    [Fact]
    public void Dynamic_dispatch_cost_and_call_counts_are_recorded_against_the_existing_light_path()
    {
        ExtraControls(); FakeDriver.Seed(Slot.ClientVoltRailsGetControl); var d = DynamicDispatcher();
        var observations = new List<object>();
        foreach (var (version, kind) in new[] { ("2", "cached"), ("2", "light"), ("2", "verify"), ("3", "verify"), ("3", "light"), ("3", "cached") })
        {
            BrokerRequest request = new(BrokerRequest.OpStatus, GpuId: "gpu-A", Kind: kind, Value: version);
            d.FromClient(request); var elapsed = new List<double>(); var counts = new List<int>();
            for (int i = 0; i < 20; i++)
            {
                FakeDriver.GetWords.Clear(); var timer = System.Diagnostics.Stopwatch.StartNew();
                var reply = d.FromClient(request); elapsed.Add(timer.Elapsed.TotalMilliseconds); Assert.True(reply.Ok, reply.Error); counts.Add(FakeDriver.GetWords.Count);
                if (kind != "verify") NoVf();
            }
            elapsed.Sort(); observations.Add(new { version, kind, samples = elapsed.Count, min_ms = elapsed[0], median_ms = elapsed[10], p95_ms = elapsed[18], max_ms = elapsed[^1], min_getters = counts.Min(), max_getters = counts.Max() });
        }
        _output.WriteLine("DYNAMIC_FAKE_DISPATCH_ONLY " + JsonSerializer.Serialize(observations)); Assert.Equal(0, FakeDriver.Sets);
    }
    private sealed class OtherGpu(string root, FakeHost host) : IGpuTarget
    {
        public string StableId => "gpu-B";
        public string Name => "Second fake GPU";
        public GpuSession Session { get; } = new(2, "fake-B");
        public OwnershipJournal Journal { get; } = new(Path.Combine(root, "ownership-B.json"), host, "gpu-B");
        public ApplySentinel Sentinel { get; } = new(Path.Combine(root, "apply-B.lock"));
    }
    private sealed class Gpus(IGpuTarget first, IGpuTarget second) : IGpuTargets
    {
        public IReadOnlyList<string> Ids => [first.StableId, second.StableId];
        public IGpuTarget? Resolve(string? id) => id == null || id == first.StableId ? first : id == second.StableId ? second : null;
    }
    [Fact]
    public void Dynamic_device_inventory_and_sample_times_remain_scoped_to_stable_GPU_identity()
    {
        string otherPath = Path.Combine(_dir, "other"); Directory.CreateDirectory(otherPath);
        var other = new OtherGpu(otherPath, _host);
        new DailyProfile([new("pstates20", "core", "0")], _host.Now).Save(DailyProfile.PathBeside(other.Journal.Path));
        var d = new BrokerDispatcher(new(FakeDriver.ApiFor(Slots.All.Where(s => s.Access == SlotAccess.Read && s.Signature == Signature.GpuStruct).Select(s => s.Slot))), new Gpus(_target, other), _host, false) { StatusClock = _clock };
        d.StatusLifecycle.InitializeStartup(["gpu-A", "gpu-B"]);
        foreach (string id in new[] { "gpu-A", "gpu-B" }) { using var ready = d.StatusLifecycle.Begin(id, RestorationKind.Startup); ready.Complete(true); }
        Advance(3); var first = Dynamic(d, "verify").Devices[0]; Advance(15);
        var secondReply = d.FromClient(new(BrokerRequest.OpStatus, GpuId: "gpu-B", Kind: "verify", Value: "3")); Assert.True(secondReply.Ok);
        var second = secondReply.Result!.Value.Deserialize<TrayStatus>()!.Devices[0];
        var reply = d.FromClient(new(BrokerRequest.OpStatus, Kind: "cached", Value: "3")); var cached = reply.Result!.Value.Deserialize<TrayStatus>()!;
        Assert.True(new TrayStatusCache().Accept(cached));
        foreach (var device in cached.Devices) Assert.Equal(device.StableId, device.Applied!.GpuId);
        Assert.Equal("240000", cached.Devices.Single(x => x.StableId == "gpu-A").Applied!.Rows.Single(r => r.Id == "pstates20/core").SavedValue);
        Assert.Equal("0", cached.Devices.Single(x => x.StableId == "gpu-B").Applied!.Rows.Single(r => r.Id == "pstates20/core").SavedValue);
        Assert.NotEqual(first.Applied!.Rows.Single(r => r.Kind == "vf").SampledAt, second.Applied!.Rows.Single(r => r.Kind == "vf").SampledAt);
        Assert.Equal(first.Verification!.At, cached.Devices[0].Verification!.At); Assert.Equal(0, FakeDriver.Sets);
    }
}
