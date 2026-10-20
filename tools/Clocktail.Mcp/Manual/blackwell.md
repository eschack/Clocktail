# Blackwell, the parts that matter
Voltage rails, offset slots and ceilings, clock domains, the V/F curve, how the firmware picks a clock, and the write-order traps.

## Voltage rails
- **NVVDD**: the core rail (GPCs, the graphics clock). Its voltage is what the V/F curve sets.
- **MSVDD**: the on-chip interconnect rail: XBAR (crossbar), L2, SYS and Video. Not the memory chips.
- **FBVDD / FBVDDQ**: the memory chips (GDDR7). Visible only as power-monitor channels; not tunable.
- NVML cannot read voltages on this card. Clocktail reads each rail's requested voltage from the driver (5 mV resolution); `gpu_status` shows both.
- Per-GPC ADCs confirm the core is not sagging under load: their mean reads 5-10 mV above reported NVVDD even at 422 A.

Typical readings on the reference card:
| State | NVVDD | MSVDD |
|---|---|---|
| Idle | ~800-835 mV | ~825 mV (its floor) |
| Stock curve, heavy load, 575 W | ~1025 mV | ~975 mV |
| Stock curve, light load | ~1090 mV | ~1040 mV |
| Tuned curve, full load at 600 W | ~915-945 mV | ~940-960 mV |
| Tuned curve, games | 925-1015 mV | ~970 mV, up to its MAX at light load |

On the stock curve MSVDD sits about 50 mV under NVVDD. On an undervolted curve it often sits above NVVDD; that is normal.

## Rail offset slots: REL, ALT/OP, OV, VMIN
Each rail has four offsets (mV, 5 mV steps):
- **REL**: the reliability limit. The lowest of REL, ALT/OP and OV is the rail's **MAX**.
- **ALT/OP**: the alternate reliability (operating) limit.
- **OV**: the overvoltage limit.
- **VMIN**: the floor. Raising it raises the minimum voltage the rail ever runs.

Stock on the reference card: **MSVDD REL is -50 mV** (not 0), so MSVDD MAX is 1045 mV; REL 0 makes it 1095 mV (ALT/OP 1115, OV 1200). NVVDD MAX is 1095 mV. Always take the card's readback as stock, never assume 0.

Ceilings Clocktail holds and you must respect:
- No raise may push a rail's MAX above min(the domain's own max, **1150 mV**). Hardware already above it (stock NVVDD OV is 1200 mV) may stay or go down.
- REL above 0 is past NVIDIA's own reliability limit. REL +20 on MSVDD tops out at 1115 mV unless ALT/OP is raised too.
- "Voltage boost" percentages had no measurable effect on this card. Do not sell them as a feature.

## Clock domains
- **GPC / graphics (core)**: follows the V/F curve on NVVDD.
- **XBAR, SYS, Video**: on MSVDD. Under load **XBAR is held to about 0.9 x the core clock** (Blender: core 3030-3052 MHz, XBAR 2722-2737). Without an offset, XBAR is limited by the MSVDD voltage it gets; that is why raising MSVDD REL speeds it up.
- **Memory**: its own offset (P0 memory offset); see `manual memory`.
- XBAR/SYS/Video each take a **clock offset** (MHz) and a **voltage demand** (-25..+50 mV, applied to MSVDD for that domain). See `manual xbar-msvdd`.

## The V/F curve
- 127 editable core points (index 0-126), about 6.25 mV apart, roughly 450 mV to 1240 mV. Clocks land on a 7.5 MHz grid; the regulator steps 6.25 mV.
- Each point has an offset (MHz) on top of its stock frequency. "Effective" frequency = stock + offset, adjusted by the driver's temperature compensation.
- **The firmware climbs the curve and stops at the first limit**: the wall, a current limit, MAX voltage, the curve's own top, temperature. At a given clock it runs the lowest voltage that reaches it.
- Under a power wall it lowers the clock; the voltage is whatever the curve gives at that clock, interpolated between two points (usually 7-15 MHz under a point).
- Stock curve: knee near 870 mV (point 67), then about 22 MHz per 5 mV, climbing all the way to MAX. A stock card that runs out of curve is at its **voltage limit**.
- A tuned curve usually has a **flat top** below MAX. A card on a flat top is at its **curve cap**, not a voltage limit.
- **Temperature compensation**: 875-915 mV runs faster when hot (+15 to +37 MHz at 82 vs 41 C); 915-940 mV barely moves; above 950 mV it runs slower when hot (-30 to -45 MHz). Steps are coarse with hysteresis (up near 57/62/72/82 C, down near 70/61/53/43 C). A cool card runs 10-40 MHz higher at the same voltage, so a flat top is flat only at the temperature it was set at.
- **Leakage**: at 600 W (930-990 mV) heat costs about 2 MHz/C; at 450 W near the knee (870-885 mV) about 9 MHz/C.

## What limits the clock above ~1.0 V
On the reference card the chip's own stability boundary flattens above ~980 mV: at light load it sits near 2960 MHz at 930 mV, 3030 at 955, 3115 at 980-985, and no higher at 1000-1005. Under heavy load the 600 W wall rarely lets NVVDD past ~955-1015 mV at all. So "stuck around 2950-3000 MHz at ~1.0 V" is usually the wall (heavy load) or the curve's top (light load), not a hidden limit; check `limiter`.

## Write order and persistence (traps)
- **Any P0 offset write (including the memory offset) zeroes every V/F point offset.** Order: power, fan curve, P0/memory, rails, voltage demands, XBAR/SYS/Video offsets, the curve last. Presets and the daily profile already apply in this order.
- A P0 core offset is refused while the curve is non-zero; core offset and point offsets share one budget. Clocktail tunes with the curve, not the P0 core offset.
- A driver reset (TDR) keeps the offsets but puts the wall back to 575 W; the service restores the daily profile. A reboot clears everything; the service writes the daily profile again at logon.
