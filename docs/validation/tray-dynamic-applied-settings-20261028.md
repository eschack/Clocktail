# Dynamic Applied settings: software evidence

2026-10-28 (client date). Branch `feature/optional-windows-tray-app`, implementation parent `a3eedca308fbaecf83bce6969e407b93dfaa85ae`. Requested plan: `docs/plans/tray-dynamic-applied-settings.md`, originally assessed on integration `6dbaf86`.

## Delivered behavior

Protocol 3 adds a GPU-scoped dynamic table covering saved/adopted targets, observed supported modifications, validated ownership and trial overrides. All 13 canonical write families have explicit display/getter decisions. Tests require an entry for every `WriteRegistry` family. Zero-valued/suspended/unreadable/unknown saved targets remain visible; unsaved cells hide only after supported readback establishes equality with an applicable reference. Failed previously modified rows retain last successful evidence. Original values are references, never current readback.

References distinguish firmware, documented neutral, captured original and adopted baseline. Old-boot/foreign/dead-owner/future originals do not establish references. Topology changes invalidate captured mappings conservatively. Unknown rail/fan/ratio defaults are not invented. Partial rail and fan targets compare specified fields only. FE absence is neutral only where the board can establish it, while saved explicit watt requests remain distinct from absence. F8 absence/non-limiting, automatic fan mode and released locks have explicit semantics.

Full checks discover supported inventory and keep the existing 127-point effective-curve proof; P0 displacement is not double-added. Light checks refresh required saved/common, visible and known-active cheap cells with zero V/F getters. Full-only rows retain real sample times. Optional exploratory read errors are coverage limitations, while missing required targets prevent a complete daily match. Newly modified unscanned external cells are discovered by the next full check.

Large row sets use byte-bounded, immutable, instance/device/generation-bound pages. Collection rejects expired, missing or duplicate data and stops subsequent pages on quiet/session/selection/disposal. Bounds and omissions are explicit; no hidden 64-control truncation remains in protocol 3. Versions 1/2 keep legacy display behavior; protocol 2 still refreshes automatically and protocol 1 remains manual-only.

The native grid uses stable row objects/keys and explicit units. Age-only updates preserve controls/handles, selection and scroll. Data refresh preserves grid rows/selection/scroll. Rail slots expand into child rows; fan and shaped V/F values have concise summaries and copyable canonical detail. The outer details page provides the single scrolling region. The compact hover remains.

## Results

| Check | Result |
| --- | --- |
| Full .NET suite | 945 passed; zero failed/skipped |
| New cases versus automatic-refresh parent | 33 |
| Focused status/refresh suite before final two coverage/power cases | 79 passed; final cases also passed in the full suite |
| Expanded fake performance fixture, separately rerun | Passed |
| npm installer/client tests | 11 passed |
| Release solution build | Zero warnings/errors |
| Clean self-contained win-x64 tray and broker publication | Passed; no installation or normal live launch |
| Synthetic UI | 21 states, dark/light/simulated high contrast; 189 PNGs |
| Native UI checks | 84 dark-view foreground checks; 17 age/data stable-row checks; pin/handle/layout and tall/short diagnostics checks |

Fake cases cover canonical family coverage, type-aware units/equality, zero/unknown saved values, adopted/partial targets, unsaved discovery, trial fan/current/F8/locks, captured-versus-current distinction, claimed unchanged dependencies, ownership context, unreadable retained rows, return to baseline, unscanned external-change limits, more than 64 required candidates, coherent pages and stale cursors, quiet mid-page, topology invalidation, GPU scope and timestamps, read budgets, unsupported interfaces, partial optional coverage, required daily proof and legacy automatic/manual compatibility. Getter tables are deduplicated; no status path calls a setter, resolves a setter, executes a write plan, settles ownership, mutates profile/journal/sentinel or starts PresentMon.

Native previews additionally exercise large inventories (43 rows in the many-settings fixture), shaped curves, long fan policies, empty/partial inventories and unavailable saved targets. An initial development `matched` diagnostics render failed its foreground assertion; reruns and the complete final batch passed. The cause is undetermined: the global assertion cannot distinguish independent desktop focus changes from app activation. These are offscreen software checks, not certification of interactive focus, screen readers, mixed DPI or the actual Windows high-contrast setting. Initial grid sizing/pin regressions were fixed before the final batch.

Raw local evidence: `out/dynamic-settings-validation/all-tests.trx`, `focused.trx`, `performance.trx`, `ui/` and `release-hashes.json`. Portable build: `out/Clocktail-Tray-0.4.1-tray-dynamic-preview.2-win-x64.zip`; broker: `out/dynamic-settings-release-v2/broker/Clocktail.Broker.exe`. The final preview pass is regenerated from the published tray. The additional unknown-profile regression preserves prior saved targets with explicit unknown/last-saved presentation when the record cannot be read.

## Fake cost comparison

Twenty warmed in-process dispatcher samples per case, same extended fake native tables. The fixture exposes a manual fan, a floor and an additional request, so its protocol 3 light set is larger than a normal saved six-control profile. Counts are identical across samples. Timings include software serialization/file-read work and exclude real pipe/driver latency and UI rendering; they are observations, not thresholds.

| Contract / kind | Native getters | V/F getters | Setters | Min ms | Median ms | p95 ms | Max ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2 cached | 0 | 0 | 0 | 0.1580 | 0.1661 | 0.3018 | 0.4273 |
| 2 light | 8 | 0 | 0 | 0.3672 | 0.3903 | 0.4952 | 0.7955 |
| 2 full | 11 | 3 bulk | 0 | 0.6248 | 0.7054 | 0.9966 | 9.1202 |
| 3 full discovery | 29 | 3 bulk | 0 | 1.2201 | 1.5316 | 2.1144 | 9.0037 |
| 3 light | 13 | 0 | 0 | 0.7948 | 0.8603 | 1.1875 | 1.3039 |
| 3 cached | 0 | 0 | 0 | 0.5211 | 0.5477 | 0.6611 | 0.6623 |

Expanded discovery has additional getter/software cost. These fake numbers do not measure real CPU time, wakeups, power or gaming impact and cannot establish performance neutrality.

## Limits and integration acceptance

No installed broker/tray, startup registration, real profile/ownership state, GPU tuning, reset or workload was changed. Integration must merge this new commit with its XBAR/P0 fixes, rebuild/install the combined release, and coordinate read-only acceptance of current saved/modified rows, units, reference provenance, Check now, several light ticks with unchanged full-only timestamps, quiet/catch-up, unchanged profile/originals and reset history. Compare live native counts/latency and broker/tray CPU/wakeups against installed `6dbaf86`.

Unknown firmware baselines remain explicitly incomplete/unconfirmed. Fan mode reports the canonical firmware level/range/tach context; this reader does not invent percentage/RPM units where the validated getter lacks that distinction. Oversized unsupported records can reach explicit cell/value/coverage limits; metadata that cannot fit a bounded response fails rather than supplying an all-clear. Newly external-modified unscanned controls and V/F-only changes need the next full discovery. Rich operation history, tuning actions, installation UI and the separate console-flash plan are outside this change.
