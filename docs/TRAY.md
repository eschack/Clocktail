# Optional Windows tray companion (initial implementation)

Clocktail.Tray is a separate, unelevated, read-only Windows application. Launching it does not apply a profile, install or start the broker, or register anything at sign-in. Exit tray closes only this companion. The broker, CLI, MCP and daily restoration continue independently. Existing npm/setup and headless release artifacts do not include or launch the tray.

This first implementation follows the selected **Profile first** design: a compact cached hover card, a click-open details panel, an applied/saved comparison, and a static effective V/F curve. GPU identity and values come from the broker. Missing values are unavailable; missing saved targets are **Not saved**. Policy remains **managed by the agent / unavailable**. A verified match is not a stability or safety assessment.

## Build and preview

Requires the repository's .NET 10 SDK and Windows for native execution.

```powershell
dotnet build src/Clocktail.Tray -c Release
# Synthetic demonstration only; no broker or GPU access:
.\src\Clocktail.Tray\bin\Release\net10.0-windows\Clocktail.Tray.exe --demo matched
```

Other `--demo` states: `trial`, `stale`, `unavailable`, `suspended`, `different`, `quiet`. Close a running preview with **Exit tray** before choosing another state. One instance runs per Windows user/session. All demonstrations are explicitly labeled synthetic. The app does not launch a demonstration unless requested.

An offscreen native render writes PNGs without a tray icon or broker connection:

```powershell
.\src\Clocktail.Tray\bin\Release\net10.0-windows\Clocktail.Tray.exe --demo matched --render C:\path\to\preview-output
```

Without `--demo`, the tray fetches a cached snapshot once at startup. It does not install or upgrade an existing broker; an older broker displays an explanation that typed status is unsupported. **Check now** reconnects when necessary and explicitly requests hardware readback for the selected stable GPU ID. Live integration should be coordinated separately from software development and synthetic preview checks.

Create a separate portable download with `build/publish-tray.ps1 -Version <version>`. The script only builds and packages; it does not install, launch or register startup. Manual removal means exit the tray and remove its extracted directory. There is no autostart registration to remove in this version.

## Status contract and read budget

`status`, version `1`, uses the existing length-bounded authenticated local named pipe. Pipe ACLs and network exclusion are unchanged. `Kind=cached` reads bounded profile/session files and the latest existing FlightRecorder sample. It does not call the GPU, settle crash records, recover, run a transaction or start PresentMon. `Kind=verify` requires a stable GPU ID and reads the effective curve plus up to 64 distinct saved/common controls. It only reads getter slots; constructed registry plans are never executed. The five-second budget stops further reads between calls; a driver call already blocked in the driver cannot be interrupted by that budget. The client closes its pipe on cancellation or an eight-second deadline.

Status requests never wait for the operation window. Cached requests during an operation return an explicit busy state with prior verification invalidated; verification returns `window_busy`. Verification also stands down for a measurement lease. Explicit hardware reads serialize with existing broker operations to avoid combining before/after values; there is no subscription holding an operation/write lock. Existing safety sampling and recovery retain their behavior.

Each reply carries a broker instance ID, monotonically increasing response revision and capture timestamp. Verification and telemetry have separate timestamps. Old revisions in one instance are rejected; reconnecting replaces prior-instance confirmations. Any mutating operation attempt invalidates broker verification, as does a changed saved profile. Startup and hovering do not implicitly verify. The app never retries or reconnects in a background loop.

Status precedence: unavailable, stale snapshot, operation in progress, record errors, unknown records, active trial, suspended daily, missing/stale verification, verified differences, unknown comparison, verified match. A snapshot or verification older than 30 seconds is stale. The icon changes once at expiry; there is no periodic hidden repaint or sampling loop. The hover dismissal timer runs only while the hover is visible. The details panel uses native accessible controls, Escape, Close, and optional Pin. Paint, hover, opening details and switching tabs read the UI cache only.

A match requires every saved canonical target to be read back, plus a consistent regional effective curve and explicit core targets. A P0/global offset alone cannot establish an effective curve match. The app plots the driver's effective frequency directly; it does not add a global offset to it again. Legacy curves or ambiguous/partial effective state remain unconfirmed. Stock comparison is omitted when the source cannot establish it. Curve source/reference details are intentionally conservative pending the shared core/curve fixes.

**Quiet for benchmark** is a manual local toggle with explicit release. It closes hover/details, stops expiry rendering and blocks new checks. It does not expire automatically, write a judge lease, suppress broker safety actions, start a benchmark, or imply a benchmark is in progress. An explicit check already sent can finish; quiet mode is not a mechanism to interrupt broker operations. No background telemetry refresh, event feed or notifications are implemented.

## Scope and validation limits

Implemented software tests use fake driver structures and isolated named pipes: cached no-driver/no-file-mutation behavior, explicit getter-only verification, effective/global disagreement, partial reads, malformed records, busy/measurement guards, cancellation, typed IPC, stale data, ordering and restart/version handling. The existing headless suite is independently runnable without a tray reference.

Native previews exercise the selected composition with synthetic values. They do not establish real-world focus, mixed-DPI, all taskbar/overflow positions, screen-reader operation, Explorer restart or performance neutrality. These need separate interactive validation. Hover uses `ShowWithoutActivation`, `WS_EX_NOACTIVATE`, `WS_EX_TRANSPARENT` and `MA_NOACTIVATE`; native controls remain interactive only in details. The MVP anchors from the icon mouse event and bounds placement to that monitor's work area; exact shell icon rectangle anchoring and comprehensive text-scaling behavior remain follow-up work.

Still deferred: opt-in installer/autostart UI, persisted preferences, broker event/reconnect feed, typed recovery outcomes/operation history, runner quiet leases, rich accessibility/placement validation and any profile actions. No apply/stock buttons or tuning controls are exposed. Profile controls depend on shared core/curve and restoration fixes, with separate consent and hardware validation. No GPU, installation, reset/reboot or benchmark validation is implied by a successful build or unit test.

The UI uses the user-selected mockup proportions and palette, with RateTray as visual/interaction inspiration (MIT, copyright 2026 nowrap). No RateTray account, provider, network polling or update implementation is included. The gauge icon is drawn locally, not bundled from an external icon pack.

## Initial validation, 2026-10-26

- Branch `feature/optional-windows-tray-app`, based on `main` at `c70a4a6`.
- Solution Release build: no warnings or errors. Focused status suite: 12 passed. Installer suite: 11 passed.
- Full .NET suite: 866 passed, 7 failed (873 total). All seven failures reproduce on an untouched export of `main`: `A_reset_s_crash_goes_to_the_trial_on_the_card_when_the_GPU_stopped_answering`. Its expected string uses the current culture's time separator; the existing production formatter uses invariant culture. These unrelated assertions have not been changed here.
- Seven synthetic UI states rendered in dark/light appearance. All 21 dark-view foreground-preservation checks passed for offscreen rendering. This is limited software evidence, not interactive hover/overflow/DPI certification.
- A separate self-contained portable preview ZIP was built locally. No app/broker installation, sign-in registration, live GPU access, profile application, reset/reboot or benchmark was performed.

The subsequent UI fix adds locally drawn gauge, pin/close, match/session, temperature/power, refresh, moon and disclosure icons. Pin/unpin updates only the existing button; it does not rebuild the panel. The offscreen interaction check toggles it 20 times and verifies the control tree, native handles and scrollbar styles are unchanged, with no scroll-container layout events. Full snapshot refreshes suspend scroll layout and redraw until the final content is ready.

Disabled action buttons retain native disabled input/accessibility behavior while drawing readable muted text and icons from the selected palette. Diagnostics is a full-height, selectable, wrapped text block with no inner scrollbars. The whole details page provides the only scroll area. Native scrolling follows the Windows app color mode through .NET 10's isolated native-theme API, with system high-contrast behavior retained. The preview checks cover expanded diagnostics, repeated disclosure toggles, and blocked clicks on the disabled Check now button.

Opening diagnostics now grows the details window to its measured content height, retaining its bottom edge and clamping it inside the selected monitor's work area. Closing diagnostics shrinks it again. Scrolling is reserved for a work area too short to fit the content. Synthetic sizing checks cover a 1200-pixel tall work area without scrolling and a 600-pixel work area with one themed fallback scroll area. Hover remains enabled for readable native text; nonactivation styles and transparent hit testing prevent it from taking focus or accepting interaction. Previews verify these styles along with enabled labels and unchanged foreground focus.
