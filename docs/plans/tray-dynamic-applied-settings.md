# Plan: dynamic Applied settings for saved and currently modified controls

Status: implemented and software-validated in the tray feature branch; merged installation and live acceptance pending.
Requested2026-10-27. Baseline: codex/test-xbar-p0-together, commit6dbaf86c78a1ec129093bea936b5d34bd077d819, installed0.4.1-tray-refresh.6dbaf86.

## Objective

Replace the fixed six-row Applied settings table with a dynamic read-only view of all broker-supported settings that are saved in the active daily profile or observed as currently modified. Show current readback beside the saved daily value, with explicit availability, source and age.

The fixed hover summary may retain its compact core/memory/XBAR presentation; the complete Applied settings tab must not silently omit other controls.

## Current gaps

- Presentation.Rows is a hardcoded list: core, memory, XBAR, ordinary power limit, legacy boost and XBAR voltage demand.
- Broker v2 reads the daily profile's non-V/F writes plus six common controls. The UI still renders only the fixed six.
- StatusControlReader already supports some additional canonical cells, but omits trial-only fan levels and rail current limits.
- DailyProfile.TrialOnly excludes fixed fan duty, current limits, F8 power requests, clock floors and voltage/Boost Lock from saved daily profiles. These controls can still be temporarily modified and belong in this view.
- OwnershipJournal records original values and recovery responsibility. It does not store authoritative current values; coupled originals may be claimed even when unchanged.
- BrokerDispatcher.Ownership() invokes Settle(), which can mutate records. A read-only table must not use that endpoint as a shortcut for discovery.
- Presentation.Format currently defaults unfamiliar numbers toMHz. A dynamic table needs explicit type-aware formatting.

## Row membership and evidence

Use canonical identity (stable GPU ID, kind, key) for deduplication and selection. A rail cell may expand into stable child rows for its individual slots; a fan cell is keyed by channel/policy identity, not a transient array position.

Discover candidates from the union of:
1. All writes in the active saved daily profile, including adopted targets and zero-valued saved targets.
2. Current-boot validated ownership cells and active trial cells/overrides.
3. A bounded, capability-aware inventory of all existing readable broker-supported cells on this GPU, to discover unsaved changes made outside Clocktail.

Read candidates through validated getter-only adapters, then display:
- Every saved target, regardless of whether it equals its baseline, is suspended, or is unreadable now.
- Every supported cell whose observed value differs from an established reference/baseline.
- Previously visible modified cells that become unreadable, as unconfirmed rows with their last evidence and error; do not make them disappear as if restored.
- Recognized active/owned cells without enough baseline/readback evidence to classify, clearly marked "Modification unconfirmed" rather than calling them modified.
- Unknown or unsupported saved/journal keys as bounded diagnostic rows, showing the saved value and why current readback is unavailable.

Hide reliably unchanged, unsaved cells by default. Remove an unsaved modified row only after fresh supported readback establishes its return to the applicable baseline; save-before/after data is insufficient by itself.

"Not saved" means no explicit target in the active daily profile. For trial-only controls show "Not saveable as daily · trial only". "Unknown" and "Unavailable" are distinct from zero, off, absent or automatic.

## Defining modified and baseline provenance

Do not infer modification from a nonzero number, a journal entry, or a difference from daily alone:
- Unmodified ordinary power is normally nonzero; clock ratios have nonzero defaults.
- Rail offsets can have nonzero firmware defaults; fan policy, stop/start temperatures and fixed duties require structured interpretation.
- A modified value can equal the saved daily, so comparing only against daily would hide legitimate rows.
- An unsaved setting can match stock but still be explicitly saved; it remains visible.
- Adoption can establish a configured baseline that differs from factory state.

Each adapter must provide typed normalization/equality and state which reference is known: firmware/OEM stock, explicit documented neutral state for that exact control/topology, or a captured pre-change original. Reuse existing stock/external-setting interpretation where valid, after separating it from mutable/MCP-specific orchestration. Never hardcode reference-card defaults for every GPU.

Keep "differs from firmware stock" separate from "differs from captured baseline" and from "differs from saved daily". If no reliable reference is available, do not manufacture one: show saved/owned/known-active rows with uncertainty and record the inventory coverage limitation.

Validate journal identity, boot and ownership context before using originals as evidence. Original values are reference evidence, never a substitute for current readback. Do not claim another tool caused a change merely because it lacks a Clocktail journal entry.

## Control coverage

Create a getter/descriptor coverage matrix tied to every canonical family in WriteRegistry.All. Adding a write family must require an explicit display/read-support decision in tests.

| Broker control family | Dynamic identity and display |
| --- | --- |
| P0 offsets | Core and memory; signedMHz. Keep P0 separate from effective V/F evidence. |
| V/F curve | One summary row plus existing curve tab; shaped/uniform summary, point count and actual full-read age. |
| Power requests | OrdinaryFE and additionalF8;W, explicit absent/non-limiting state; F8 marked trial only. |
| Rail adjustments | NVVDD/MSVDD rel, alt, ov, vmin slots; signedmV with provenance and per-rail grouping. |
| Clock-domain offsets | XBAR, SYS and video entries exposed by topology; signedMHz. |
| Voltage demand | Graphics, XBAR, SYS and video supported mappings; signedmV and correct target rail. |
| Voltage boost | Independent and legacy paths separately;percent. |
| Fan control | Per supported channel;auto or manual duty/RPM as reported; fixed duty trial only. |
| Fan policy | Per editable policy; structured temperature/duty/RPM points, with full detail on demand. |
| Fan stop/start | Per policy/channel mapping;stop/start temperatures in°C. |
| Clock limits | Maximum/minimum frequency and voltage/Boost Lock;MHz or mV, off/released correctly identified. Floor/point locks remain trial only. |
| Clock relationship | Supported XBAR/core relationship;ratio/multiplier decoded from canonical U16.16 representation. |
| Rail current limits | Supported regulator/rail identity;mA/A with range/availability evidence;trial only. |

"Supported" means the broker recognizes the control and the current GPU/driver/topology exposes a validated read path. Policy refusal to raise a setting must not prevent displaying a readable current value. Do not probe arbitrary IDs/registers or invent unsupported channels to fill the table.

Saved values must use the same canonical decoding as readback. Handle partial structured requests explicitly: unspecified rail slots are not implicit zero targets. An absent power request is not0W. A released lock is not a locked0MHz. Use canonical semantic equality, not formatted string equality.

For implicit P0-only daily profiles, a V/F row may say "Not explicitly saved; effective target follows saved core". Explain the relationship without inventing a saved point-curve encoding or adding mirrored P0/point offsets twice. Keep full effective-curve proof from the preservation fix unchanged.

## Data model and broker implementation

Add domain-level read descriptors/presentation metadata beside the canonical write definitions or in a linked catalog with exhaustive coverage tests:
- Stable kind/key, group/order, label, type/unit, supported-key discovery and daily eligibility.
- Getter-only adapter, typed value/equality, baseline/provenance and optional expansion into child rows.
- Freshness class: lightweight-capable or full/discovery-only.

Do not enumerate controls by parsing WritePath.Keys prose or calling WriteRegistry.Prepare(). Prepare can read V/F, resolve setters or construct write plans. A display reader must never perform a setter, equal-value proof, transaction, recovery, profile adoption or journal mutation.

Use a bounded read-only journal/file snapshot, not the mutating ownership endpoint. Reuse the existing operation/measurement guards, five-second read budget, client cancellation and table deduplication. Fan/current-limit discovery must use existing validated getter paths and explicit bounds; expose no new arbitrary driver/bus access.

Return typed row records with:
- Canonical identity and optional parent/child identity, display group/label/unit.
- Explicit saved target/presence, daily eligibility and suspended-profile context.
- Current value or unreadable/unsupported state, last successful sample time and latest attempt/error.
- Reference value/provenance, modification classification and current-versus-daily comparison.
- Read class, broker instance, generation and inventory/discovery timestamp.

Keep the existing verified daily comparison separate from exploratory row coverage. An unrelated unavailable control should not turn a proven saved daily into a failed restore; a missing saved target still prevents claiming a complete daily match. Display partial inventory/read coverage explicitly.

Extend the typed protocol through capability negotiation or a new version where required. New tray with older broker falls back to the fixed legacy display with a coverage explanation; old clients retain their current contract.

## Refresh and discovery policy

Preserve the automatic-refresh design already installed:
- Full startup verification, tuning-change completion, recovery completion and Check now perform a capability-bounded inventory/discovery pass and the required full curve read.
- Lightweight15second refreshes read saved, visible modified and known-active lightweight-capable cells, plus existing required common controls. Deduplicate tables across rows.
- Known unchanged inventory entries can remain outside each light tick; new external changes in those entries are discovered at the next full inventory. State this detection limit alongside the inventory age.
- V/F and other full-only rows retain their own real sample time across light refreshes. Never treat an older full value as a newly sampled light value.
- A generation change invalidates comparisons; retain visible rows as pending/unconfirmed until appropriate readback completes.
- Quiet mode, session lock/suspend, measurement leases, busy operations, one in-flight request and bounded coalescing/backoff continue to apply. No extra discovery loop runs behind quiet mode.
- Native topology changes, reconnect or GPU selection reset the correct device inventory/proof without leaking another GPU's rows.

Avoid silently truncating the current64-control lists. Establish explicit descriptor/key/row/response limits and handle larger inventories through bounded batches or pagination. Expose incomplete coverage; complete daily match is not justified if a saved target was omitted. One full operation may collect a bounded multi-batch inventory, with coherent generation/context checks and honest per-row timestamps.

This view means all discovered supported saved/modified settings as of their displayed readback/discovery times. It cannot guarantee instant detection of arbitrary external changes while avoiding scans. Check now requests current complete supported inventory subject to explicit read limits; no new periodic full V/F polling is included.

## UI behavior

Replace Presentation.Rows iteration in Applied settings with the typed dynamic collection. Keep the primary columns:
- Control
- Applied (readback)
- Saved daily

Add compact state/age/source detail through a secondary line, status marker or accessible disclosure. Distinguish Matches daily, Differs from daily, Unsaved change, Trial only, Unconfirmed and Unavailable. Use text as well as color.

Group controls with stable ordering (core/VF, memory, power/rails, uncore, fans/limits) and stable row keys so a refresh preserves scroll position, keyboard focus, selection and disclosures. Avoid rebuilding the entire native control tree for age-only updates.

Support long fan curves and shaped V/F values with concise summaries and expandable/copyable detail. Retain the curve plot; do not dump127 offsets into the table by default. Use accessible labels, high contrast and light/dark themes, readable unavailable states and one appropriate scroll region for large lists.

When no cells qualify, show "No saved or confirmed modified settings" only if inventory coverage is sufficient; otherwise show the discovery/unavailable explanation. Unsupported saved rows remain visible. No apply/reset/save actions are added.

Examples:
- Saved core+240MHz reads+240MHz: visible, matches daily.
- Saved core0MHz reads0MHz: visible because saved.
- Unsaved video+100MHz reads+100MHz against a known0MHz baseline: visible, Not saved.
- Unsaved stock legacy boost0% with known baseline0%: hidden.
- Fan channel manually set to50% in a trial: visible, AppliedManual50%, SavedTrial only.
- Saved power450W is unreadable: visible, AppliedUnavailable, Saved450W.
- Unknown baseline for an owned ratio cell: visible/unconfirmed with captured-original provenance; no universal0.9 assumption.
- Full V/F read is4minutes old but current light controls are fresh: V/F row retains its own4minute age.

## Implementation sequence

1. Define row membership/evidence semantics, baseline provenance and exhaustive coverage matrix; audit existing stock/external/read-only helpers.
2. Add canonical typed descriptors, safe getter adapters and unsupported/error handling for every family, including trial-only controls.
3. Add bounded candidate discovery, journal/trial/saved union, baseline comparison and typed protocol/capability response.
4. Integrate full/light scheduling, generation invalidation and independent timestamps without changing profile/tuning behavior.
5. Replace fixed table rendering with stable grouped dynamic rows and type-aware formatting; preserve compact hover layout.
6. Add focused fake tests, synthetic layouts and performance evidence, then run full suites and publish Release.
7. After reviewed implementation, merge/build/install and perform coordinated read-only live acceptance with existing daily settings.

## Validation

Headless/fake tests:
- Coverage test accounts for every WriteRegistry family/key category; no family silently lacks a display/read decision.
- Union/deduplication for saved, owned, trial and externally modified cells; zero-valued saved targets stay visible; unchanged unsaved cells hide.
- Claimed but unchanged dependencies do not become false modified rows; baseline/ownership attribution remains honest.
- Trial-only fan/current/F8/locks display correctly without being added to a daily profile.
- Typed formatting/equality for negative offsets, ratios, partial rail values, absent requests, auto/off modes and structured fan/VF state.
- Read failures retain saved/previously modified rows and ages; recovery/reconnect/selection cannot resurrect stale rows.
- Zero setters, write-plan calls, ownership settlement, profile/journal mutation and PresentMon starts across all display paths.
- Cached requests stay GPU-free; light requests make zero V/F getters; full requests retain all127-point proof. Native table reads are deduplicated.
- Quiet/busy/measurement, cancellation, bounded inventory, partial coverage, reply ordering, protocol fallback and generation changes are covered.
- Separate row coverage errors from required saved-target proof; truncated/missing saved targets never produce an all-clear result.

Synthetic UI:
- Existing daily, extra uncore/rails, manual fan/current/locks trial, shaped VF, long fan curves, many channels, empty/partial inventory, unavailable saved cell and older full values.
- Light/dark/high contrast, long labels, tall/short work areas, focus/scroll retention and age-only updates.

Live acceptance after installation:
- Current saved daily rows and all known modified controls appear with correct units, provenance and timestamps.
- Existing profile files and ownership originals remain intact; no new driver-reset events.
- Check now discovers supported inventory; several light ticks preserve full-only timestamps.
- Measure native call counts/latency and tray/broker CPU/wakeups against the installed refresh baseline; quantify the cost of expanded discovery rather than assuming it is free.
- Use fixtures for additional fan/current/rail/lock changes. Do not alter live tuning, induce a reset or run workloads solely to populate the table without separate user authorization.

## Scope

This plan extends tray-automatic-refresh.md and is independent of broker-startup-console-flash.md. It covers visibility/readback only, with no new tuning controls, profile persistence policy or automatic corrective writes.

Implemented by the commit containing this plan. No installed binary, GPU setting, startup task, reset or workload was changed in this session. The integration session owns merged publication/installation and coordinated live acceptance.

## Implementation record, 2026-10-28

- `AppliedCatalog` accounts for every canonical `WriteRegistry` family; tests fail when a new family has no display decision. `StatusControlReader` discovers only validated exported getter/topology paths, including trial-only controls. Canonical semantic equality handles partial rail/fan targets, ratios, absent requests and released/automatic modes.
- Protocol 3 carries GPU-scoped typed rows, saved presence, per-slot rail targets, references/provenance, modification/daily comparisons, successful sample/attempt timestamps, generation, discovery age and explicit coverage. Versions 1/2 remain available with a fixed legacy table and compatibility explanation.
- Saved/adopted, validated ownership, legacy/generic trial overrides and full supported inventory form the candidate union. Originals never replace current values. Foreign/dead/old-boot/future references are excluded; topology changes invalidate captured mappings. Unknown saved/owned keys remain diagnostic rows. Fresh baseline returns remove unsaved rows; failures retain last successful evidence.
- Full checks discover inventory; light checks cover saved, visible and known-active cheap cells without V/F calls. All native requests are deduplicated and use the shared read budget. Read-only byte-bounded pages avoid a hidden 64-control cutoff; page collection verifies device/instance/generation and stops follow-up requests on quiet/session/selection/disposal.
- Stable native grid rows retain selection and scroll. Age-only updates preserve controls/handles. Grouped values use explicit units, full-only timestamps, accessible text and detail/copy disclosure. Large inventories use the single outer scroll area.
- Full .NET suite: 945 passed; installer/client suite: 11 passed. Thirty-three new cases cover the dynamic contract and related scheduling/compatibility. Twenty-one synthetic states pass in dark, light and simulated high contrast; 84 dark-view foreground checks and 17 stable-row update checks pass. Release solution and clean self-contained tray/broker publication pass.
- Limits remain explicit: incomplete/unsupported topology or references are disclosed; fan mode uses the canonical firmware level without invented duty/RPM units; V/F-only and newly modified unscanned external cells require the next full discovery. Live driver latency, CPU/wakeup and power/workload impact were not measured.
- See [software evidence and remaining live acceptance](../validation/tray-dynamic-applied-settings-20261028.md).
