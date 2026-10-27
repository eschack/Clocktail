# Tray automatic refresh: software validation

2026-10-27. Branch: `feature/optional-windows-tray-app`, parent `075cbe9`.
Scope: implementation, fake native tests, isolated IPC, native synthetic UI and Release publication. No installed application, startup registration, live GPU setting, workload or reset was changed or invoked.

## Behavior delivered

- Resident 15-second current-control checks; 60-second cached inventory. A full effective curve is read after completed startup restoration, settled changes, completed recovery, unlock/resume continuity loss and Check now.
- Current controls and full curve retain separate timestamps. A light match does not renew old curve proof. Generation, instance or context changes invalidate match; UI receives invalidation before a slow full follow-up finishes.
- Startup readiness begins before serving the pipe and spans complete restoration. Failed restoration stays failed; status never retries a restore or applies settings.
- Quiet, session lock/disconnect, suspend, measurement leases and busy operations guard requests. Sent calls may finish, then follow-ups stop. Retries use bounded 15/30/60-second backoff; missed ticks do not replay.
- One request in flight, cancellation, click coalescing, per-GPU proof, old-reply rejection and version 1 manual-only fallback.
- Version 2 hardware status uses explicit getter descriptors. No generic write-plan preparation or setter resolution. Trial-only fan level/current-limit cells are intentionally excluded; unsupported records remain unconfirmed.
- Implicit P0 proof checks effective-minus-regional-base displacement, preserving compatibility with integration's mirrored-P0 correction. Explicit curves retain all-point control/effective comparison.

## Checks

| Check | Result |
| --- | --- |
| `dotnet test tests/Clocktail.Tests -c Release` | 912 passed, 0 failed, 0 skipped |
| Refresh/status/recovery subset of that suite | 58 passed; 37 newly added cases |
| `node --test npm/test/clients.test.js` | 11 passed, 0 failed |
| `dotnet build Clocktail.slnx -c Release` | Passed, 0 warnings/errors |
| Portable tray self-contained win-x64 publication | Passed; separate preview ZIP |
| Broker self-contained win-x64 publication | Passed; separate build-only output |
| Native synthetic UI | 12 states, dark/light, 96 PNGs, 48 foreground-preservation checks |

Covered fake cases include zero cached GPU calls/file mutations; zero light V/F calls; common native-table deduplication; full 127-point proof; doubled implicit displacement; explicit curve changes; missing common/curve reads; partial masks; measurement/busy deferral; lifecycle invalidation during native readback; delayed startup and queued recovery; observer-independent existing recovery; generation/record/control changes; quiet before startup and during a sent call; locked/resume behavior; UTC jumps with monotonic scheduling; bounded reconnect/defer; old-broker compatibility and manual failures; cancellation; malformed typed state; stale generations/instances; GPU selection while an older reply is in flight; early UI invalidation.

Synthetic states: matched, trial, stale, unavailable, suspended, different, quiet, startup pending, current controls with an older full curve, changed controls, recovery, and incompatible broker. Every state exercises hover enabled text, nonactivation/input transparency, 20 pin toggles with the same control tree/handles and no scroll-layout events, diagnostics expansion without scrollbars in a tall work area, and one fallback scroll area on a short work area. Quiet Check now rejects clicks. Offscreen previews do not certify interactive focus/taskbar/overflow/DPI/accessibility behavior.

One initial published `unavailable` render failed the foreground-preservation assertion (confirmed in the .NET Runtime event). An isolated rerun and the subsequent complete 12-state published batch passed. The assertion observes the desktop foreground window and cannot distinguish independent desktop focus changes from activation caused by the app; the cause of that initial change is undetermined.

Local raw evidence is under `out/automatic-refresh-validation/`: `all-tests.trx`, `focused.trx`, and `ui/`. Final full results include the version 2 isolated-pipe and empty-inventory backoff cases added after the earlier focused run. Preview artifacts are regenerated from the published tray. Portable output: `out/Clocktail-Tray-0.4.1-tray-refresh-preview.2-win-x64.zip`; broker: `out/automatic-refresh-release-v2/broker/Clocktail.Broker.exe`.

## Fake dispatch timing and native-call counts

Thirty warmed in-process dispatch samples per kind, measured with `Stopwatch` against the same fake daily structures. These exclude real named-pipe round trips, UI work and real driver latency. Values below are from the final full test run and are diagnostic observations, not performance thresholds.

| Kind | Getter calls | V/F calls | Setters | Min ms | Median ms | p95 ms | Max ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Cached | 0 | 0 | 0 | 0.1804 | 0.1932 | 0.3491 | 0.4008 |
| Light | 8 | 0 | 0 | 0.3458 | 0.3740 | 0.6579 | 0.7402 |
| Full | 11 | 3 bulk | 0 | 0.8852 | 1.0574 | 1.4700 | 1.5343 |

All 30 samples have the stated getter counts. The fake scheduler preserves one full timestamp across sixteen subsequent light ticks over four minutes. Fake quiet periods of one hour produce zero requests; exiting quiet performs one catch-up sequence. This establishes scheduling and slot behavior, not real CPU/wakeup/power cost.

Additional regression cases verify that empty GPU inventory backs off rather than producing a zero-delay wakeup loop, and that exhausting the five-second read budget stops later getters and prevents entering V/F readback. Failed full requests are labeled as attempts, and a curve-only mismatch is a known difference rather than an unknown result.

## Remaining live acceptance

Integration must merge the new commit with its XBAR/P0 fixes, rebuild and install the combined release, then coordinate cold-boot startup, several minutes of light refresh, Check now and quiet/catch-up checks. Retain real driver-call/round-trip evidence and compare broker/tray CPU time and wakeups enabled versus paused. These measurements were not performed here, and no claim of idle power or gaming/benchmark neutrality is made.

An external tool changing only V/F points without changing lightweight controls cannot be detected between full checks. The UI keeps the last actual full timestamp visible and offers Check now for current complete evidence. The initial 15-second cadence is a policy choice pending live performance measurements. Rich operation history, a broker event feed and opt-in installer UI remain outside this change.
