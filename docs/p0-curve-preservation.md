# P0 offsets and effective V/F preservation

This fix starts at `38ef607f5d8e163fec42ae29d8f596e3243b4224` on
`fix/p0-curve-preservation`. It is separate from device-limit consent. The
consent checks, policy scopes, device ranges and global-plus-point firmware
validator remain in force.

## Software evidence

The first six fake-driver regressions failed four cases on the baseline:
an isolated core write lost the effective increase in an overlapping model,
an existing curve did not receive the requested effective change in that
model, and daily/ownership expectations failed under both models. These
tests do not establish which native call caused the reported hardware loss.

`EffectiveP0Tests` uses the real broker with two deliberately hypothetical
driver models. Both read the raw P0 value dynamically:

- Additive: effective frequency = stock base + P0 core + point control.
  A P0 request clears point controls.
- Overlapping: effective frequency = stock base + point control. A native
  core request mirrors its target into point controls; memory clears them.

The tests isolate core and memory, check all points of a nonuniform shape,
raise and lower core targets, and return to originals. They cover refused
core/memory/curve writes, failed native and effective readbacks, failed
rollback, live-owner refusal, dead-owner adoption, thermal base changes,
combined-budget refusal, a later failure in a multi-write trial, and daily
save/recovery/reapply entirely against fakes. MCP tests reject a broker's
success when effective frequencies did not change, and accept matching
effective results even when control bytes changed.

Validation on 2026-10-25:

```
dotnet test tests/Clocktail.Tests -c Release --no-restore --nologo
# 993 passed, 0 failed, 0 skipped
dotnet build Clocktail.slnx -c Release --nologo
# 0 warnings, 0 errors
```

## Write and ownership behavior

A preserving P0 trial holds one GPU lock and sentinel across original
capture, native write, any curve repair, verification and rollback. The V/F
original is on disk before the first possible P0 side effect. An existing
journal original always wins.

For core, the target is the pre-operation effective curve shifted by the
change in the raw P0 target. For memory, the effective core curve is kept.
After the native call, regional readback determines the missing change:
Clocktail adjusts point controls by that observed error, rather than adding
the requested offset to both representations. The correction must match
clock-step quantization; only one repair is attempted, through the normal
V/F path and bounds. Both the P0 field and effective frequencies must then
verify. Regional effective-minus-base comparisons avoid converting thermal
base changes into saved offsets; the reporting tolerance is under 1 MHz,
smaller than a full 7.5 MHz clock step.

Legacy bases are derived from effective frequency minus stored controls.
They cannot identify the P0 contribution independently. Legacy writes
restore the prior controls and verify direct effective frequencies; they
fail and roll back when that result cannot be established. Adaptive repair
from a derived legacy baseline is deliberately unsupported.

V/F ownership remains while a P0 cell is held, even if point controls equal
their original: restoring P0 can change those controls again. Daily capture
retains this explicit V/F dependency, including a zero curve. The trial
records the verified final control encoding. Recovery keeps P0-first,
V/F-last ordering; trial rollback uses that order too and verifies the
whole coupled result. Unverified rollback retains originals and the
sentinel, and reports an unknown write outcome. Verified rollback reports
that nothing remains written.

## Native uncertainty and coordinated validation

No hardware writes, native profile reloads, installation, service changes,
resets, reboots or benchmarks were performed. The reported first full
readback followed the complete sequence, so it cannot distinguish loss
during core from loss during memory. Neither fake model is proven native
behavior. In particular, the historical workaround reporting P0 +240 and
V/F +240 with effective +240 is evidence against blindly assuming an
additive model, but does not identify every intermediate native state.

With fresh explicit user coordination, validation should:

1. Capture a known starting state and the current journal/profile. Read
   raw P0, V/F status and controls immediately before core, immediately
   after the native P0 setter, and after final preservation. Check all
   effective points, voltages, bases, controls and ownership.
2. Capture the same observations for an isolated memory write, and then
   memory following core. Do not infer the core result from the combined
   sequence. Check that memory preserves the verified effective core curve.
3. Exercise an agreed existing curve and lower core target, then save and
   reload an agreed profile. Verify that its explicit V/F dependency and
   true original survive. A rollback drill needs its own agreed failure
   method and restoration plan.
4. Coordinate any deployment, service restart, reset/reboot or benchmark
   separately. Software results do not establish performance or stability.

Historical journal entries with a wrongly captured original cannot be
reconstructed from the current card alone; this fix preserves existing
originals rather than guessing replacements.
