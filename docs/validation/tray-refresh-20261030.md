# Tray measurement status and stable refresh - 2026-10-30

Implemented the approved TRAY-STATUS-REFRESH-PLAN-20261030.txt on feature/optional-windows-tray-app, parent e7c7fdcaf178d8a2fe2145443531d0176b5eb37c.

## Behavior

- Existing MeasurementBusy now has explicit measurement-paused presentation. Last-known dynamic readback is labeled unconfirmed with its sample age; missing prior data is named, and saved daily targets remain intact. Check now is disabled/explained during measurement and quiet mode. Recovery, record failures and disconnection retain precedence. Measurement release returns to pending verification until existing broker evidence permits a match. No protocol, scheduling or hardware-read changes.
- Routine details refresh updates bound labels, button state, glyphs, curve data and grid rows in place. The structure key excludes telemetry, generation, status state, quiet/busy and tab choice. GPU/schema, diagnostic expansion and presence of error/compatibility content can rebuild atomically. Detachment is inside the redraw guard. Routine pinned/unpinned bounds remain stable; real content growth uses the outer scroll area. User tab/diagnostic changes still resize. Grid height changes are batched, and inserted rows are retrieved from the grid to retain valid native row ownership.
- Normal grid selection uses the surface/ink colors, with native keyboard focus cues. Actual Windows high contrast retains Highlight/HighlightText. Right-click selects the intended cell before details/copy. Existing keyboard navigation, Ctrl+Enter, double-click and context actions remain enabled; tooltip behavior is retained.

## Validation

- Release solution build: zero warnings/errors.
- Full .NET suite: 1096 passed, zero failed/skipped; includes new measurement state/release/error/recovery/disconnection checks and existing no-driver-reads-during-measurement checks.
- Installer suite: 11 passed.
- All 23 synthetic preview states passed, including measurement with invalidated last-known rows and no-prior-readback. Final affected matched/measurement/measurement-empty previews reran after added assertions and encoding corrections.
- 32 repeated native refresh updates across pinned/unpinned and both tabs, diagnostics expanded and a small monitor: telemetry, values, generation, ages 9/10/99/100 seconds, measurement entry/exit. Control tree/handles, bounds, scroll, current cell and tab state stayed stable. Row add/remove and enlarged control-scale checks passed; deactivation hides only unpinned details.
- Measurement assertions confirm explicit last-known/no-prior text, retained saved core target and disabled Check now. Existing tooltip/palette/cleanup, diagnostics, pin and dynamic-grid checks retained.
- Evidence: V:/agents/clocktail/tray-refresh-20261030/final. Measurement details preview visually inspected. git diff --check passes.

Development checks caught and fixed a shared-row reference error exposed by deferred grid sizing, an incorrect test assumption about row ordering, and encoding artifacts in new UI strings. Final relevant checks pass.

## Limits and handoff

These are software/offscreen checks, not proof that desktop flicker is eliminated. Enlarged control scaling is not a real mixed-monitor DPI transition. Installed interactive focus, keyboard/screen-reader behavior, context-menu copy, actual high-contrast mode and perceived flicker remain acceptance checks. Existing native controls and input handlers are preserved.

No installation, installed-component restart, live GPU workload or tuning was performed. Notify the originating integration chat after commit/push. It may merge and build; installation still requires the user's explicit approval.
