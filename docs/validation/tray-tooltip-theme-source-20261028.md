# Applied settings tooltip theme fix

2026-10-28. Branch `feature/optional-windows-tray-app`, parent `812d01ce6c79edf2f93b26ca8cc8780d511b9ccb`.

Status: build/test hold explicitly lifted by the human. Source and software validation complete; integration owns the combined build and installed-app replacement.

The reported bug is a large light/native-styled cell tooltip over the dark Applied settings panel. The user clarified that apparent poor contrast in the supplied screenshot is HDR tonemapping. This fix does not compensate for HDR, alter the app palette or change display settings.

## Cause and change

`AppliedSettingsGrid` previously assigned its details payload to every cell's `ToolTipText`. DataGridView's built-in tooltip paints separately from the styled cells and does not inherit their palette.

The grid now disables that built-in tooltip and owns one reusable owner-drawn WinForms `ToolTip`. It paints surface, text and border from the selected palette. Actual Windows high contrast takes priority through `SystemColors.Info`, `InfoText` and `WindowText`. Native tooltip font, sizing and hover timing remain in use; no new focusable popup, polling loop or custom timer is introduced.

Cell entry supplies the existing full details payload. Leaving/hiding the grid, removing the hovered row or changing GPU clears the tooltip. Age/data updates reuse the component instead of recreating it. Grid disposal disposes it. Keyboard/context-menu detail and copy paths remain available. No broker/GPU/protocol/settings logic changes.

## Validation

| Check | Result |
|---|---|
| Release build, `src/Clocktail.Tray` | Passed, 0 warnings, 0 errors |
| Release .NET suite, `tests/Clocktail.Tests` | 945 passed, 0 failed/skipped, including the timestamp cases |
| Installer suite, `node --test "npm/test/*.test.js"` | 11 passed |
| Offscreen synthetic previews | All 21 states passed |
| Tooltip regression reports | 38 dark/light checks; 34 additionally exercise actual cell-enter/leave events and full detail payload registration/cleanup |
| Tooltip color/draw checks | Palette and Windows high-contrast branch, painted surface/border pixels, built-in native cell tooltips disabled |
| Dynamic update checks | 17 passed; grid rows/handles, selection, scroll and tooltip component identity preserved |
| Foreground checks | 84 passed, 0 failures |
| `git diff --check` | Passed |

`PreviewRenderer` now performs the tooltip checks and saves painted detail samples. Evidence: `V:\agents\clocktail\tray-tooltip-theme-20261028\validation\final` (223 PNGs, including 34 direct tooltip-paint samples). Dark/light painted samples were visually inspected. These are draw-path samples, not captures of interactive native tooltip windows.

An initial `matched` preview exited with a CLR failure code before completing the light-theme checks; its stderr was not captured, so the cause is unknown. An immediate captured rerun passed, followed by the complete 21-state captured batch with no failures. The initial build exposed a nullable tooltip-text warning, fixed before the clean final build.

The .NET suite ran immediately before the final tray-only warning/regression additions; those additions were compiled and exercised by the native preview batch. No domain/test-project source changed afterward.

## Integration acceptance

Software checks do not certify actual desktop hover timing, native wrapping/placement, mixed DPI, screen readers or Windows high contrast enabled at OS level. Verify those against the combined installed build. No installed broker/tray, startup registration, GPU/profile state, tuning, reset or workload was changed in this session. Integration will review/merge, build and install the combined release after the ready notification. The previous hold is lifted; no upstream PR is requested.
