# Presets
The starting points that ship with Clocktail, what each part of one does, and how to offer it.

## founders (RTX 5090 only)
The reference card's daily profile, kept after four days of measurement. In write order:
| Setting | Value | Why |
|---|---|---|
| Power limit | 600 W | the board maximum; the last 100 W buy ~10 % frames |
| Fan curve | 50 C 20 %, 79 C 60 %, 86 C 100 % | holds ~79-80 C at 600 W, under the 82 C compensation step |
| Memory offset | +358 MHz | ~2.4 % lower memory latency; bandwidth unchanged |
| MSVDD offsets | REL 0 (stock -50), ALT 0, OV 0, VMIN 0 | XBAR runs faster; ~+2 % in Blender |
| XBAR voltage demand | +20 mV | keeps MSVDD off its floor (no cliff) |
| XBAR clock offset | +300 MHz | with the demand: Blender ~6-7 % faster than stock |
| V/F curve | stock to 800 mV, +405..+510 MHz at 850-885 mV, then +2 MHz/mV to 3071 MHz at 1035 mV, flat above | ~+200 MHz over stock under the wall |

Verified on the reference card: bit-exact compute tests, nvgwls at 50 % and 100 % load, and games (Kingdom Come: Deliverance II, Resident Evil Requiem, The Last of Us Part II, Cyberpunk 2077; 5-8 minutes each, no crash; 39-74 MHz under the crash line).

## Offering it
- `list_presets` says whether this card is the model a preset was tuned on; `load_preset` refuses any other model.
- Ask first. Say what it changes (the table above, in a sentence), that it replaces their daily profile (the old one is kept in the tuning log), and that their chip may not hold it.
- After loading: `stress_test load=100` for 60-120 s, then load 50 for 60 s, then have them play. If anything fails, it is their silicon, not the preset (and `back_to_daily` now means the preset). Lower the curve where it failed (`begin_session`, `adjust_curve` -15 MHz, test, `keep_as_daily` once it holds).
- If lowering the curve does not cure it and the failure was memory-bound or showed artefacts, XBAR +300 may be too much for this chip: `adjust_xbar` -50 MHz per step, test, `keep_as_daily` once it holds.
- From stock without a preset is equally valid; it just takes more sessions.
