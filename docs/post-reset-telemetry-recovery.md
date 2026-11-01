# Post-reset invalid telemetry recovery

Base: installed/current b175b336b2946a04b67f284a34d7f6c785a94e87. Fix branch codex/post-reset-telemetry-recovery. The pre-existing untracked console-flash plan is untouched. No installed binaries, processes, profile or startup configuration changed.

## Findings
The provided incident records a responsive worker publishing impossible successful telemetry while fresh processes read normally. Source accepts successful NVML outputs without checking ranges; utilization>=15 blocks idle qualification indefinitely (up to four hours). This establishes a validation/recovery defect, not the native cause. Scalar output locals and utilization stack buffers were also not initialized: a successful native return without filling its outputs could expose undefined values. That is a possible mechanism, not demonstrated incident provenance. No evidence establishes a marshaling or race defect; reproducing the native origin would need controlled tracing in a separately approved validation session.

## Candidate
NVML utilization, temperature, power and clock reads initialize outputs to impossible sentinels and reject broad impossibility bounds. They latch native state lost and throw the existing NvmlException. FlightSampler catches it, publishes unknown for that field and subsequent fields, and avoids further native calls. The same utilization reader serves the idle gate. Existing WatchDriver observes loss, closes the serialized dispatcher with its existing10s write-drain bound, then terminates without native teardown. The supervisor restarts with existing2/5/15/30/60s capped backoff. This does not guarantee survival of a native access violation already in flight.

Unreadable idle samples break the three-sample streak. Six consecutive unknown/invalid samples at5s cadence fail the wait and request the same loss/handover path. Busy/idle/unreadable transitions and minute reminders are logged. No unknown sample qualifies as idle. Valid busy GPUs still wait at most4h. The startup path now refuses profile application if leftover recovery did not succeed. Existing journal/sentinel reconciliation, game recovery, crash attribution and operation guards remain in use; no direct profile write shortcut.

Bounds are deliberately broad: utilization0..100%, temperature0..150C, clocks<=100000MHz, power<=10000000mW. These are corruption screens, not thermal/power safety limits or proof a plausible sample is fresh. In-range stale telemetry cannot be identified by these checks alone. A lost latch suppresses subsequent reads; readings already in flight may complete.

## Software evidence
Added fake-native successful-invalid-utilization reproduction; failed on original implementation, passes with latch/no-further-native-call/no-shutdown checks. Added six-unusable-sample bounded handover, interrupted idle streak/eventual restoration, and failed leftover recovery blocking profile tests. Existing full suite includes supervision/backoff, exceptions, reset retries/bursts and P0/VF restoration.1293tests pass. No physical driver reset was induced. Broader fault-injection combinations and native root-cause tracing remain useful follow-up validation; test coverage is not a claim to recreate the proprietary driver fault.

## Proposed live validation — requires separate permission
1. Preserve current state/logs and install reviewed binaries only after user approval; separately authorize broker restart and restoration of the saved profile. Check safe current state/no active game/session/measurement first.
2. Observe fresh worker identity and startup reconciliation. Confirm three valid idle samples before full daily restoration, original journal values, all127effectiveVFpoints, core/memory/XBAR/power, and savedprofile byte preservation.
3. Do not induce a reset by default. On a naturally occurring reset, capture logs/worker identities and readbacks. Invalid telemetry must be unknown; fresh-worker handover must be bounded with backoff, no repeated crash attribution, and restoration must wait for idle and release of measurement guards.
4. If invalid state persists in fresh workers or recovery stays partial, stop validation and inspect; do not bypass the gate or repeatedly reapply settings. A deliberately induced reset/reboot or workload requires fresh explicit authorization.
5. Verify tray recovery/paused states, no impossible numeric output, restored settings and no restart loop. Keep results separate from stability/performance claims.
