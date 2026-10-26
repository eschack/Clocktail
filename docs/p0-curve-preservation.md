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

Validation after the review fixes on 2026-10-26:

```
dotnet test tests/Clocktail.Tests -c Release --no-restore --nologo
# 1049 passed, 0 failed, 0 skipped
dotnet build Clocktail.slnx -c Release --nologo
# 0 warnings, 0 errors
```

## Write and ownership behavior

A preserving P0 write holds one GPU lock and sentinel across original
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

## Startup evidence and daily restoration follow-up

The supplied `tuning-log.md`, `profile.json` and `ownership.json` under
`%LOCALAPPDATA%/Clocktail/pci-2c0210de-89de1043-00002c02-b01-s00/` were read
without modifying them. At 2026-10-25 22:04:05 Europe/Copenhagen (21:04:05Z),
the installed `0.4.0-fork.7a0bfd8-prompt1` build logged core 0 -> +240 MHz,
then aborted memory before its setter because all 127 V/F offsets were
nonzero. Power 450 W and XBAR +300 MHz applied. The saved daily contains
core +240, memory +3000, power 450 W and XBAR +300, without `vf/core`.
The startup journal holds power/core/XBAR originals and no V/F original.
The supplied subsequent readback reports +240 on all V/F points and memory
0, with no temporary trial or new recorded reset.

This narrows this startup failure: a successful native memory write did not
produce the nonzero controls that its guard refused. Core's native side
effects are consistent with that state. It does not retrospectively isolate
the original explicit-setter sequence or prove every native intermediate.
The earlier successful save/readback and Speed Way result remain separate
observations; no additional workload was performed here.

The first fix, `9d96bd0`, still missed this path. Of the first ten new
startup regressions, seven failed on it: the overlapping model could apply
memory but lose effective core tuning, reapplication could give back the
generated curve, and partial-reporting coverage was incomplete. Three
further regressions exposed matching raw P0 values masking a lost curve or
retaining a temporary point-curve trial during back-to-daily.

Daily restoration now uses the coupled path for profiles without an
explicit V/F entry. A saved P0 core target with no point curve means a
uniform effective displacement from the independent regional base; it is
verified and repaired even if raw P0 already matches. Memory then preserves
that verified effective curve. A memory-only profile preserves the captured
original core shape, giving back any unkept point-curve trial. An explicit saved V/F entry remains authoritative and is written
last. A derived legacy base cannot prove the implicit uniform target, so
that core group fails actionably rather than guessing; an explicit verified
curve is needed for that legacy profile.

The generated V/F state remains an implicit daily dependency across startup,
resume and after-reset application. This does not rewrite the saved profile
during application. A later explicit keep captures its verified V/F entry.
Dropping a failed explicit core/VF group still restores its true original;
it is not reclassified as memory's implicit curve. Native-generated curves
with a captured stock original are attributed to Clocktail. A pre-existing
external curve retains its actual original and attribution.

Partial responses retain `success=false`, identify omitted groups, and
include requested/current values and readback availability. Logs say
`Daily profile partly applied` for a completed degraded application and
`Daily profile application incomplete` otherwise. They no longer assume an
omitted setting's original was stock. An unverified coupled rollback stops
later groups and reports an unknown outcome with the sentinel retained.

`StartupP0Tests` adds twenty fake-driver cases covering the supplied profile,
both control models, repeat application, a matching raw target with lost
effective tuning, back-to-daily from a point-curve trial, partial and unknown
outcomes, nonstock originals, attribution, explicit curves, legacy refusal,
and software-only keep/recovery/reload.

## Review regressions addressed on 2026-10-26

The review of `d62ac1e` supplied three additional fake-driver reproducers.
All twelve initial in-repository variants failed on that tip. They exposed
premature ownership release, a memory-only daily retaining a newer curve
trial, and a full-status prerequisite blocking an ordinary return to zero.

Coupled writes now retain both cells' originals through every intermediate
write and rollback. Only after the complete effective/P0 proof succeeds are
eligible originals released, P0 first and V/F second. Returning to zero and
then failing the final proof therefore leaves the original records intact
while rolling back to the previous tuning; a NoChange rollback setter cannot
lose the V/F original. Subsequent recovery remains possible.

Only an implicit **core** target protects a generated daily curve during
profile restoration. A memory-only daily restores the captured original
curve before preserving it for memory; it does not adopt a later temporary
point curve. Tests cover both zero and nonzero originals under both models,
including the trial leaving the record and recovery of the true original.

Ordinary `ClockOffsets.Apply`, broker `OpClock`, and raw P0 `OpWrite` now
capture the coupled original from validated Info/Control topology and
offsets, without requiring effective Status. The same control snapshot
supplies the firmware budget. Registry preparation carries readable coupled
originals to the transaction's write-ahead callback. An existing original
still wins; an unreadable control never becomes an invented original. The
existing unknown-budget rule still bounds core to a move toward zero and
retains existing debt. Adaptive P0 preservation still requires full curve
readback, and V/F writes still require full validation and firmware bounds.

`P0ReviewRegressionTests` adds twenty-two cases. Besides the initial twelve,
they cover release only after a successful final proof, preservation and
recovery of previously recorded nonzero originals, allowed zero returns and
refused moves away from zero with unreadable controls, and continued refusal
of V/F writes when full Status is invalid. All 1,035 software tests and the
Release solution build passed. These findings and fixes remain software
evidence; they do not establish native semantics or hardware stability.

## Second-review topology regression addressed on 2026-10-26

The second review of `03de151` confirmed that the three preceding findings
were fixed, but exposed another budget failure: matching Info/Control masks
could expose readable nonzero Core offsets while using an unsupported
ownership topology. The ownership reader rejected that topology after
parsing the offsets, and its caller mistakenly downgraded the whole budget
to unknown. A return-to-zero request could then bypass the nonzero-curve
wipe guard and erase an external curve without a captured V/F original.

Budget data and ownership capability now remain separate results of the
same Info/Control read. Validated offsets stay known when the mask or Core
sequence cannot encode a recoverable original. Nonzero offsets continue to
trigger the existing wipe refusal. Even with zero offsets, a changed P0
request is refused if those readable controls cannot be recorded for
recovery. An equal-value request remains NoChange. Only controls that cannot
be read/validated use the existing unknown-budget fallback; its bounded
core return-to-zero behavior remains covered by the prior review tests.

`P0TopologyRegressionTests` adds fourteen cases. Twelve changing-write cases
failed on the reviewed tip. They exercise actual Apply/OpClock/OpWrite with
unsupported masks and noncontiguous Core entries, each with zero and nonzero
offsets. They now refuse before any fake setter runs, retain the exact
control buffer, and preserve known budget offsets separately from the
unavailable ownership snapshot. Two equal-value cases remain permitted
without writes. All 1,049 software tests and the Release solution build
passed. The new case is fake-driver evidence, not an observed native driver
transition. No hardware operations, commits or pushes were performed for
this second-review follow-up.

## Native uncertainty and coordinated validation

This software investigation performed no hardware writes, native profile
reloads, installation, service changes, resets, reboots or benchmarks. The reported first full
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
   Include startup restoration of the existing P0-only saved daily, verify
   memory +3000 and effective core +240 independently, and inspect generated
   V/F ownership and partial-failure reporting. These checks remain pending.

Historical journal entries with a wrongly captured original cannot be
reconstructed from the current card alone; this fix preserves existing
originals rather than guessing replacements.
