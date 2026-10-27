# Plan: automatic tray refresh with event-triggered full V/F verification

Status: implemented and software-validated on the tray feature branch; merged installation and live acceptance pending.
Requested 2026-10-27. Baseline: codex/test-xbar-p0-together at 9f07e85344911d6d7fe7a5c3c3aaca7f9d753cc3.

## Objective

Keep the tray useful after login without repeated manual Check now clicks. Perform lightweight automatic refreshes and full V/F verification after startup, tuning changes, recovery and Check now. Respect quiet mode throughout.

Preserve the distinction between current control readback and the last complete effective-curve verification. Refreshing cheap data must not silently renew proof about the curve.

## Baseline behavior and evidence (before implementation)

- TrayContext.FirstIdle() fetches cached status once; only Check now invokes Fetch(true).
- The expiry timer changes presentation but does not request another snapshot.
- TrayStatus.FreshFor is30seconds and currently ages both the outer snapshot and full verification with that same threshold.
- Kind=cached reads bounded records and existing FlightRecorder telemetry without driver reads.
- Kind=verify calls VfReader.Read(), verifies saved/common controls, then the tray requests cached inventory again.
- VfReader.Read() fetches point information, status and control in three bulk native calls. It does not issue127 per-point calls.
- The current dispatcher invalidates cached proof on top-level mutation attempts using _statusEpoch, but this token is not exposed to the tray; recovery completion/readiness is also not explicitly modeled.
- Cold boot83 automatically restored the correct daily and started the tray, but the tray could not become green until the user clicked Check now.

Three idle-time verify+cached round trips on this RTX5080 took12.5,7.5 and6.8ms. These measure response time, excluding UI rendering; they do not establish CPU, power or frame-time impact, or quantify the V/F portion separately. Evidence: C:/Users/ESchack/.codex/validation/tray-check-impact-20261027/timing-result.json.

## Refresh policy

| Request | Trigger | Reads and result |
| --- | --- | --- |
| Cached | Discovery, inventory, busy fallback | Broker records and existing telemetry only; never adds GPU sampling. |
| Lightweight | Every15seconds while connected, unlocked and outside quiet/measurement mode | Current non-V/F saved/common controls, broker lifecycle/change markers, records and existing telemetry. No V/F Info/Status/Control getters. |
| Full | Startup restoration completes; tuning changes settle; recovery completes; user presses Check now | Effective V/F curve and all required saved/common controls; establish a new timestamped full comparison. |

The15second cadence is an initial default to validate, not a measured performance optimum. Keep the30second snapshot freshness threshold initially so two missed refresh opportunities produce an honest stale state. Do not solve this by simply extending the existing verification timestamp.

Automatic refresh runs while the tray is resident, including when hover/details are closed. Painting, hovering, tab changes and opening details read the UI cache. No timer-driven UI reconstruction when nothing displayed has changed.

Check now always requests a full verification for the selected stable GPU ID, even if a recent light check passed. Existing busy, locked and quiet restrictions apply. No separate rapid telemetry polling or new broker sampling loop is introduced.

## Truthful status and green indicator

Split freshness/confidence into independent fields:
- StatusCapturedAt / light-check timestamp: when current records and controls were read.
- FullVerifiedAt: when the complete effective curve was last read and compared.
- VerifiedGeneration and current broker/GPU generation: whether a Clocktail mutation or recovery has invalidated that result.
- Current comparison/error: whether light controls agree now, plus pending full-verification reason.

After successful startup restoration and full verification, the icon can become green automatically. Subsequent light checks maintain a current controls-match indication and retain the original FullVerifiedAt. Use explicit wording such as "Controls match · curve verified4minutes ago" so green does not imply that the curve was reread on each refresh.

The existing single30second expiry must no longer classify the entire display as stale solely because full verification is older than30seconds. Stale still applies to missing current status. Keep the curve age independently visible; an old curve may be described as last verified, never freshly verified.

Remove the match indication immediately on a generation change, broker instance change, observed control difference, recovery/apply in progress, unknown/error result or stale current status. Show Checking/Verification pending while a new full check is due. An active trial retains trial presentation; verification must compare against the correct current context and must not describe an intentional trial as a restored daily.

This policy cannot detect an external tool changing only V/F points between full checks while leaving all lightweight controls unchanged. State that limit in diagnostics/documentation. Check now provides current full evidence. Periodic full V/F polling is outside the requested default; add it only as a separately chosen policy, not a hidden fallback to the old30second full-read cadence.

## Detecting the full-verification triggers

Use typed broker state in lightweight responses rather than parsing logs or trusting a UI-only timer.

1. Startup and reconnect:
   - Discard confirmations from a prior broker InstanceId and discover the current GPU inventory.
   - Expose startup-restoration readiness/outcome per GPU. The pipe becomes available before ApplyAtStart finishes; pipe availability alone must not count as completed restoration.
   - Perform one full check once startup restoration is complete and the operation/measurement guard permits.
   - Display pending, suspended, failed or unavailable appropriately if restoration has not succeeded. Verification must never apply or retry the profile itself.

2. Tuning changes:
   - Expose mutation generation and completion/in-progress state per GPU; conservatively treating all relevant GPUs as invalidated is acceptable until per-GPU scoping is proven.
   - Invalidate at mutation attempt, including failed/aborted attempts. Preserve this conservative behavior from _statusEpoch.
   - Coalesce a sequence of writes into one full check after operations settle and the measurement guard releases; do not verify midway through a coupled transaction.
   - Detect saved-profile changes through a bounded fingerprint/comparison even when files change outside the dispatcher.
   - A lightweight control difference also invalidates the match and schedules one full diagnostic check, without corrective writes.

3. Recovery:
   - Track recovery generation plus pending/completed/failed outcome for startup recovery, driver reset, wake/resume and daily restoration paths.
   - Invalidate before recovery; verify only after the broker's recovery sequence finishes.
   - Cover DriverResetResponder and internal dispatcher requests, not only client-originated operations.
   - Broker worker replacement also changes InstanceId and follows the startup/reconnect path.
   - On unlock/resume, refresh current status and perform one full check if continuity was lost or a recovery/change marker requires it.

4. Check now:
   - Bypass the automatic schedule and request a full check when permitted.
   - If another check is already in flight, coalesce the user request into at most one required full follow-up. Never run concurrent verification.

Light polling may discover a settled change up to one refresh interval later. A subscription/event feed is not required for the first implementation. If finer reaction time is needed later, reuse the same typed generations and pending-check policy.

## Quiet mode, measurement guards and scheduling

- Entering Quiet for benchmark stops all automatic status requests, reconnect attempts, expiry repainting and hover/details activity. Manual Check now remains disabled.
- Preserve the real timestamps while quiet; do not fabricate fresh status because presentation is paused.
- A request already sent may complete. Entering quiet prevents subsequent requests and does not claim to interrupt a native call already inside the driver.
- Changes/recovery while quiet are caught by generation/instance checks when quiet ends. Exit quiet with one refresh and, if due, one full verification; do not replay every missed tick.
- Broker measurement leases block all additional driver reads, including the new lightweight endpoint. Current cached-only reads remain getter-free; the tray should defer scheduled work rather than repeatedly probing a busy benchmark.
- Use nonblocking operation acquisition. Do not queue status behind tuning writes, hold the window between checks, bypass judge.lock or alter existing safety/recovery behavior.
- Session lock/logoff/disconnect pauses scheduling; unlock/reconnect follows continuity checks above.
- One in-flight request per tray; collapse timer ticks, clicks and change notices. Bound reconnect/backoff (for example15/30/60seconds), reset after success, and stop promptly on disposal.
- A busy response means deferred, not failed daily restoration. Keep one pending full reason and retry at a bounded opportunity after the guard releases, without a tight loop.
- Schedule against monotonic elapsed time; use UTC timestamps for displayed evidence. Handle clock jumps, sleep and missed ticks without request bursts.
- Multi-GPU state remains keyed by stable ID. Start with selected-device hardware checking and bounded inventory refresh; switching devices must not copy another GPU's proof or timestamp.

## Broker/protocol work

Add an explicitly lightweight status path rather than repurposing Kind=cached to make driver calls.

- Build an allowlisted non-V/F getter path for current saved/common controls. Deduplicate identical native table reads, such as core and memory fields from the same Pstates20 snapshot where supported.
- Do not rely on generic write-plan Prepare methods without proving they make no hidden V/F reads. Test the actual slot calls.
- Reuse current FlightRecorder samples for temperatures, utilization and power telemetry; keep each sample's original timestamp.
- Explicit curve targets cannot be freshly compared by lightweight checks. Carry forward the separately timestamped full comparison only while its generation/context remains applicable.
- Preserve bounded record sizes, response sizes, read budgets, cancellation and the effective-curve correctness rules from the preservation fix.
- Expose supported capabilities, request version, broker instance, current generation, busy/measurement state and restoration readiness/outcome.
- Prefer a version2 contract for the new semantics. Preserve version1 behavior for old clients and have the new tray fall back to the current manual-only behavior with a clear explanation on an old broker. Do not silently reinterpret version1 full verification.
- A response revision orders replies; it is not itself a tuning-change token.
- Ensure an older asynchronous response cannot restore green after a newer invalidation. Compare request context/generation as well as response ordering.

## Files likely affected

- src/Clocktail.Domain/Broker/BrokerDispatcher.Status.cs: lightweight getter path, capability/state markers, separate proof handling.
- src/Clocktail.Domain/Broker/BrokerDispatcher.cs and recovery/startup integration: conservative invalidation and completion markers.
- src/Clocktail.Broker/Program.cs and DriverResetResponder integration: startup/wake/reset readiness/outcome.
- src/Clocktail.Domain/Broker/TrayStatus.cs and BrokerProtocol.cs: versioned models and separate freshness/confidence policy.
- src/Clocktail.Tray/TrayContext.cs and StatusSource.cs: scheduler, full-check triggers, cancellation, reconnect/backoff and quiet/session handling.
- src/Clocktail.Tray/Presentation.cs, StatusForm.cs and AppIcon.cs: clear confidence, curve age, pending/busy/quiet state.
- tests/Clocktail.Tests/Domain/TrayStatusTests.cs plus a headless scheduler test seam: native-slot budgets, generations and timing.
- docs/TRAY.md: update the manual-only behavior, confidence semantics and read budget.

Keep the scheduler/policy testable without referencing WinForms in the headless suite. Use fake time and fake sources; UI timers should only drive that policy.

## Verification and acceptance

Software checks:
1. Cached requests still make zero driver calls and no persistent mutations.
2. Lightweight requests make zero V/F Info/Status/Control calls and zero setters; common native reads are deduplicated as intended.
3. Full verification retains all127-point effective-curve proof and rejects wrong, double-counted or partial tuning.
4. One automatic full check follows settled startup, each coalesced change/recovery and Check now. No full request occurs merely because a15second tick elapsed.
5. Light refresh updates status age without changing FullVerifiedAt; generation/instance/profile change immediately invalidates full-match presentation.
6. No startup full check falsely confirms before delayed ApplyAtStart/recovery completes.
7. Quiet/locked/measurement/busy/disconnected behavior, in-flight completion, deferred catch-up, reconnect/version fallback, missed ticks and disposal obey the policy.
8. Async ordering and multi-GPU selection cannot resurrect old proof or mix GPUs.
9. Synthetic UI covers startup pending, current light+older full, matched, trial, changed, recovery, stale, quiet, unavailable and incompatible broker.
10. Run the full .NET and npm installer suites and publish a clean Release after focused tests pass.

Performance validation:
- Measure cached, light and full requests separately with the same methodology and record driver-call counts and round-trip distributions.
- Compare broker/tray CPU time and wakeups over an idle interval with refresh enabled versus paused. Do not equate wall-clock latency with CPU usage.
- Verify no extra GPU reads in quiet mode. Do not claim gaming/benchmark neutrality from idle timing; any workload comparison is separately coordinated.

Live acceptance after a reviewed implementation/install:
- One user-coordinated cold boot: daily restores correctly; tray starts and becomes verified automatically without Check now.
- Leave it for several minutes: light status stays current, FullVerifiedAt remains the true last full check, and no repeated full V/F polling occurs.
- Check now produces one new full timestamp.
- Quiet stops requests; leaving quiet performs one catch-up sequence.
- Validate tuning/recovery transitions using fakes first. A cold boot covers startup restoration; do not induce a live driver reset or change tuning solely to exercise the UI without separate authorization.
- Retain source/build/test evidence, installed hashes and logs; verify saved profile/ownership and reset history remain correct.

## Scope

This is a separate feature plan from broker-startup-console-flash.md. They can share a later release but should have independent implementation and acceptance evidence.

Implemented by the commit containing this plan. No installed binary, startup task or GPU setting was changed in the implementation session. The integration session owns the combined build/install and separately coordinated live acceptance.

## Implementation and software acceptance, 2026-10-27

- Version 2 has explicit cached/light/full paths, capabilities, current-controls and full-curve timestamps, mutation generations and typed startup/recovery states. Version 1 remains available; an old broker selects manual-only behavior.
- `StatusControlReader` uses a native getter allowlist and deduplicates identical requests. The common fake daily requires eight getters for light status and eleven for full status; light makes zero V/F calls and every status path makes zero setters. Manual fan level and rail current-limit controls are trial-only and deliberately outside the daily reader.
- `BrokerStatusLifecycle` spans complete startup/reset/resume sequences, including idle waits. Dispatch invalidates conservatively on top-level mutation attempts, internal operations, record changes and observed control changes. A two-second settle interval coalesces changes.
- `TrayRefreshController` uses fake-testable monotonic scheduling: 15-second light cadence, 60-second inventory, 15/30/60-second guarded/reconnect backoff, one in-flight request, coalesced manual checks and cancellation. Quiet/session/suspend guards prevent subsequent requests. Selection and reply ordering preserve per-GPU proof.
- 912 .NET tests and 11 installer tests passed. Within the .NET suite, 58 refresh/status/recovery cases passed, including 37 newly added cases. All 12 synthetic states pass native preview checks. Release solution build and separate portable tray/broker publication passed.
- The current-versus-full confidence wording and the external V/F-only detection limit are visible in UI/diagnostics and documented in `docs/TRAY.md`.
- See [validation evidence](../validation/tray-automatic-refresh-20261027.md). Fake dispatch distributions and call counts are recorded; live round-trip, CPU/wakeup, power and workload comparisons were not measured. Live cold-boot/quiet/Check now acceptance remains with integration after installation. No live reset or tuning action was used to exercise the implementation.
