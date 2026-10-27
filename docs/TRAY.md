# Optional Windows tray companion

Clocktail.Tray is a separate, unelevated, read-only Windows application. Launching it does not apply a profile, install or start the broker, or register anything at sign-in. Exit tray closes only this companion. The broker, CLI, MCP and daily restoration continue independently. Existing npm/setup and headless release artifacts do not include or launch the tray.

The **Profile first** design provides a compact cached hover card, a click-open details panel, an applied/saved comparison, and the last fully verified effective V/F curve. GPU identity and values come from the broker. Missing values are unavailable; missing saved targets are **Not saved**. Policy remains **managed by the agent / unavailable**. A match is not a stability or safety assessment.

## Build and preview

Requires the repository's .NET 10 SDK and Windows for native execution.

```powershell
dotnet build src/Clocktail.Tray -c Release
# Synthetic demonstration only; no broker or GPU access:
.\src\Clocktail.Tray\bin\Release\net10.0-windows\Clocktail.Tray.exe --demo matched
```

Other `--demo` states: `trial`, `stale`, `unavailable`, `suspended`, `different`, `quiet`, `startup`, `older-curve`, `changed`, `recovery`, `incompatible`. Close a running preview with **Exit tray** before choosing another state. One instance runs per Windows user/session. All demonstrations are explicitly labeled synthetic. The app does not launch a demonstration unless requested.

An offscreen native render writes PNGs without a tray icon or broker connection:

```powershell
.\src\Clocktail.Tray\bin\Release\net10.0-windows\Clocktail.Tray.exe --demo matched --render C:\path\to\preview-output
```

Without `--demo`, the tray discovers the broker and requests lightweight control readback every 15 seconds while resident, including when both windows are closed. It performs a full effective-curve verification after startup restoration completes, settled tuning changes, completed recovery, continuity loss on unlock/resume, and **Check now**. It does not install or upgrade the broker. A version 1 broker uses manual-only **Check now** with a visible compatibility notice; a broker without typed status needs a separate upgrade. Live integration is coordinated separately from software development and synthetic previews.

Create a separate portable download with `build/publish-tray.ps1 -Version <version>`. The script only builds and packages; it does not install, launch or register startup. Manual removal means exit the tray and remove its extracted directory. There is no autostart registration to remove in this version.

## Status contract and read budget

`status`, version `2`, uses the existing length-bounded authenticated local named pipe. Pipe ACLs and network exclusion are unchanged. Version 1 remains available with its original manual verification and 30-second proof freshness semantics.

| Kind | Trigger | Reads |
| --- | --- | --- |
| `cached` | Discovery, inventory every 60 seconds, guarded retry | Bounded profile/session records, lifecycle/generation markers, existing FlightRecorder telemetry. Zero GPU calls. |
| `light` | Every 15 seconds when permitted | Up to 64 distinct current saved/common non-V/F controls. Zero V/F getters and setters. |
| `verify` | Completed startup/recovery, settled changes, continuity loss, Check now | Current controls plus three bulk V/F Info/Status/Control calls covering the full effective curve. Zero setters. |

Hardware requests require the selected stable GPU ID. The light path uses an explicit getter allowlist: Pstates20, clock-domain info/control, power-policy info/control, boost control, voltage-rail info/control/status, fan-policy control, clock-limit status, and clock-relationship info/control. Identical native requests are deduplicated within a check, including core/memory and XBAR offset/demand. It constructs cell read descriptors directly and never invokes generic write-plan preparation or resolves setters. Unsupported saved controls remain explicitly unavailable. Manual fan level and rail current-limit cells are trial-only (`DailyProfile.TrialOnly`), so they are outside this daily status reader.

The five-second budget stops additional control getters between calls and prevents entering the full curve reader after that budget is exhausted; a native call already blocked in the driver cannot be interrupted. Full V/F uses the existing three-call bulk reader. The client closes its pipe on cancellation or an eight-second deadline. Cached status never settles crash records, recovers, runs transactions, starts PresentMon, or initiates telemetry sampling.

Status requests never wait for the operation window. Version 2 returns typed operation/measurement deferral with no driver calls while guarded; version 1 retains its existing busy error behavior. The broker checks live measurement leases independently of a caller's lease, so status cannot bypass them. Hardware reads serialize with existing broker operations to avoid combining before/after values; no subscription holds a lock between checks. Existing safety sampling and recovery retain their behavior.

Each version 2 reply separates capture time, current-controls time, full-curve time, current/verified mutation generation, startup/recovery readiness and measurement/settle state. Response revisions order replies; they are not mutation tokens. Mutating attempts (including failures), internal recovery, external profile/session record changes and observed control changes invalidate old full proof. Mutations are conservatively global; each GPU retains its own settings and full timestamp. A two-second quiet period after operations/lifecycle changes coalesces bursts before verification. Startup is marked pending before the pipe serves requests, and remains pending through idle waits and the complete restoration sequence. Failure is displayed and never triggers corrective writes or automatic restoration retries.

Old revisions, regressing generations and replies from retired broker instances are rejected. Switching GPUs discards any in-flight old selection reply. The scheduler permits one request at a time, coalesces clicks, and retries discovery or guarded work at 15/30/60 seconds, capped at 60. It uses monotonic elapsed time for scheduling and UTC timestamps for evidence. Missed ticks produce one catch-up sequence. An old broker remains manual-only even after a failed manual check.

A missing/current snapshot or current control readback older than 30 seconds is unavailable/stale. Startup pending, recovery, failed restoration, operation/measurement guards, trials, suspension, control differences and unknown reads all prevent a daily-match indication. After a full match, subsequent matching light checks keep **Daily controls match** current without changing **Curve last verified**. Full proof may be older than 30 seconds while current controls stay fresh and its generation/context is still applicable. Invalidation is published before a slow follow-up full request, so old green evidence disappears promptly. Telemetry retains its own original sample time.

A failed/partial full request is labeled **Full check attempted**, with its error retained in diagnostics; it does not claim successful curve verification. A known full-curve difference is shown as settings differing even when the lightweight controls match.

**Limit:** an external tool changing only V/F points, while leaving all lightweight controls unchanged, is not detected between full checks. The curve remains last verified, not continuously verified. **Check now** requests new complete evidence. There is no periodic full V/F polling.

Hidden windows are not reconstructed on timer ticks. Visible content is rebuilt only when displayed values, state or age text changes, with exact timestamps included when diagnostics is open. The icon changes once at freshness expiry. The hover dismissal timer runs only while visible. Details uses native accessible controls, Escape, Close, and Pin. Paint, hover, opening details and switching tabs read the UI cache only.

A full match requires every saved canonical target plus required common controls to be readable, and a consistent regional effective curve tied to explicit core targets. An implicit P0 daily compares actual effective-minus-regional-base displacement; mirrored point/P0 controls are not added twice. Explicit saved curves compare their point offsets and effective state. The plot uses the driver's effective frequency directly. Legacy curves and ambiguous/partial readback remain unconfirmed. Stock comparison is omitted when its source cannot be established.

**Quiet for benchmark** is a manual local toggle with explicit release. It closes hover/details, stops all automatic requests/reconnects and expiry repainting, and disables Check now. It does not expire automatically, write a judge lease, suppress broker safety actions, start a benchmark, or imply a benchmark is in progress. A sent request can finish; quiet prevents follow-up calls. Leaving quiet performs one current refresh and a full check if needed. Session lock, disconnect, logoff and suspend pause scheduling; unlock/resume refreshes continuity. There is no extra telemetry sampler, event subscription or notification feed.

## Scope and validation limits

Software tests use fake native tables, fake monotonic time, fake sources and isolated named pipes. They verify getter counts, zero V/F calls in light mode, zero setters, file immutability, full 127-point comparison, startup/recovery ordering, generation races, quiet/measurement/session guards, coalescing, backoff, cancellation, multi-GPU selection and old-broker fallback. The headless suite is independently runnable without a tray reference. See [automatic refresh evidence](validation/tray-automatic-refresh-20261027.md) for results and unmeasured live acceptance items.

Native previews exercise the selected composition with synthetic values. They do not establish real-world focus, mixed-DPI, all taskbar/overflow positions, screen-reader operation, Explorer restart or performance neutrality. These need separate interactive validation. Hover uses `ShowWithoutActivation`, `WS_EX_NOACTIVATE`, `WS_EX_TRANSPARENT` and `MA_NOACTIVATE`; native controls remain interactive only in details. The MVP anchors from the icon mouse event and bounds placement to that monitor's work area; exact shell icon rectangle anchoring and comprehensive text-scaling behavior remain follow-up work.

Still deferred: opt-in installer/autostart UI, persisted preferences, broker event subscription, detailed operation history, runner quiet leases, rich accessibility/placement validation and profile actions. No apply/stock buttons or tuning controls are exposed. Live latency, CPU/wakeup, power and workload impact need separate measurements; fake latency does not establish performance neutrality. No installation, real GPU, reset/reboot or benchmark validation is implied by successful software checks.

The UI uses the user-selected mockup proportions and palette, with RateTray as visual/interaction inspiration (MIT, copyright 2026 nowrap). No RateTray account, provider, network polling or update implementation is included. The gauge icon is drawn locally, not bundled from an external icon pack.

## Initial validation, 2026-10-26

- Branch `feature/optional-windows-tray-app`, based on `main` at `c70a4a6`.
- Solution Release build: no warnings or errors. Focused status suite: 12 passed. Installer suite: 11 passed.
- Original full .NET suite: 866 passed, 7 failed (873 total). All seven failures reproduced on an untouched export of `main`: `A_reset_s_crash_goes_to_the_trial_on_the_card_when_the_GPU_stopped_answering`. Its expected string used the current culture's time separator; production used invariant culture. They were subsequently fixed in `075cbe9`, before the automatic refresh implementation.
- Seven synthetic UI states rendered in dark/light appearance. All 21 dark-view foreground-preservation checks passed for offscreen rendering. This is limited software evidence, not interactive hover/overflow/DPI certification.
- A separate self-contained portable preview ZIP was built locally. No app/broker installation, sign-in registration, live GPU access, profile application, reset/reboot or benchmark was performed.

The subsequent UI fix adds locally drawn gauge, pin/close, match/session, temperature/power, refresh, moon and disclosure icons. Pin/unpin updates only the existing button; it does not rebuild the panel. The offscreen interaction check toggles it 20 times and verifies the control tree, native handles and scrollbar styles are unchanged, with no scroll-container layout events. Full snapshot refreshes suspend scroll layout and redraw until the final content is ready.

Disabled action buttons retain native disabled input/accessibility behavior while drawing readable muted text and icons from the selected palette. Diagnostics is a full-height, selectable, wrapped text block with no inner scrollbars. The whole details page provides the only scroll area. Native scrolling follows the Windows app color mode through .NET 10's isolated native-theme API, with system high-contrast behavior retained. The preview checks cover expanded diagnostics, repeated disclosure toggles, and blocked clicks on the disabled Check now button.

Opening diagnostics now grows the details window to its measured content height, retaining its bottom edge and clamping it inside the selected monitor's work area. Closing diagnostics shrinks it again. Scrolling is reserved for a work area too short to fit the content. Synthetic sizing checks cover a 1200-pixel tall work area without scrolling and a 600-pixel work area with one themed fallback scroll area. Hover remains enabled for readable native text; nonactivation styles and transparent hit testing prevent it from taking focus or accepting interaction. Previews verify these styles along with enabled labels and unchanged foreground focus.
