# Clocktail MCP server

The agent's hands. Each change is a **trial** in a session; the person plays, the agent records the outcome, and only what survives becomes the daily profile.

`npx -y clocktail setup` installs it and connects your agents. By hand: it is a stdio MCP server, so point any MCP client at `clocktail-mcp.exe` (`%LocalAppData%\Programs\Clocktail` after setup, or `tools\Clocktail.Mcp\bin\Release\net10.0` after `dotnet build -c Release tools/Clocktail.Mcp`). The service must be installed: it does every write, and `install_service` installs it.

## Tools

| Tool | What |
|---|---|
| `card_report` | 🩺 How's my card doing: every setting as original → now, the change and a ✅ / ⚠️ / ⛔ safety mark, live readings, one verdict. |
| `manual` | The agent's manual: the method phase by phase and why, Blackwell, reading the card, stability, presets, troubleshooting. Its short version is the server's instructions. |
| `install_service` | Install or update the service (one UAC prompt). Every write tool needs it. |
| `gpu_status` | Where the card runs now: voltages, clock, the curve point, power vs limit. |
| `card_settings` | Everything set on the card, next to stock and the daily profile. |
| `limiter` | What holds the clock back right now. |
| `curve_around` | Curve points near a voltage. |
| `watch` | Sample one stretch of play: p10/p50/p90 of clock, voltage, XBAR, power, temperature; time on the wall. |
| `live_start` / `live_read` / `live_stop` | Watch a whole session of play live: 4 samples a second, a line every 10 s under load (clock, voltage and curve point, MSVDD, XBAR, power vs the wall, temperature, fans, what held the clock), and a line when load starts or stops, the clock freezes, the card stops answering or runs hot. The log can be followed as it is written (Claude Code's Monitor); every sample goes to a CSV. |
| `list_presets` / `load_preset` | The profiles that ship with Clocktail; load one as the daily profile. Ask the person first. |
| `set_daily_power_limit` | The daily power limit. Ask the person first. |
| `begin_session` / `end_session` | Start a session for a game / end it and go back to the daily profile. |
| `adjust_curve` | Raise or lower the V/F points around the working point (one trial). |
| `set_fan_curve` | The firmware fan curve (one trial). |
| `set_memory_offset` | The memory offset; the curve is written back right after (one trial). |
| `adjust_msvdd` | Raise or lower MSVDD's REL and VMIN offsets (one trial). |
| `adjust_xbar` | XBAR's clock offset and voltage demand (one trial). |
| `set_power_limit` | Move the power limit for a trial, to run the curve lower down. Never kept. |
| `set_current_limit` | Lower the NVVDD or MSVDD current limit for a trial. Never kept. |
| `set_fan_speed` | Hold the fans at a fixed duty for a trial (thermal tests). Never kept. |
| `stress_test` | Run NVIDIA's `nvgwls.exe` on what is on the card; a failure is recorded as the trial's crash. |
| `mark_stable` / `mark_crash` | Record the trial's outcome. |
| `back_to_daily` | Put the daily profile back now. |
| `stock` / `load_daily` | The card to stock and kept there (the daily profile suspended) / the daily profile back on. |
| `keep_as_daily` | Keep what the trials set as the daily profile (not the trial wall, current limits or fixed fans). Ask the person first. |
| `data_points` | Every trial so far, summarized per curve point. |
| `log_note` / `read_log` | The tuning log. |

Rule of thumb: the highest clock `stress_test` passes, minus 30–60 MHz, is what games hold.
