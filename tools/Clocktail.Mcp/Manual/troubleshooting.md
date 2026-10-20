# Troubleshooting
When a tool fails, when the card or the service misbehaves, and where things live.

| Symptom | What it means | What to do |
|---|---|---|
| "the Clocktail service does not answer" | the service is not installed or not running | `install_service` (one admin prompt); if it was installed, tell the person to run `npx -y clocktail verify` |
| A write is refused with a limit in the message | the firmware's own limit or Clocktail's ceiling (1150 mV, current defaults, 400-600 W) | do not work around it; explain the limit |
| A write is refused because a stress run is measuring | the service stands down while a stability run holds its lock | wait for the run to end |
| `load_preset` refuses | the card is not the model the preset was tuned on | tune from stock |
| Screen froze ~30 s then came back | a driver reset (TDR); the daily profile is already back on | `data_points`, step down, see `manual stability` |
| Game closed or froze, no driver reset | a crash the service cannot see | `mark_crash` with what they saw |
| Readings all zero or tools time out right after a driver reset or a device restart | the driver is reloading | wait 10-20 s; if the MCP server itself died, the person reconnects it in their agent (Claude Code: `/mcp`) |
| Card at stock after a reboot although a daily profile is kept | the service writes it at logon once the GPU is idle | wait a minute; `card_settings`; if still stock, `npx -y clocktail verify` |
| Power limit back to 575 W after a crash | normal: a driver reset restores the stock wall | the service puts the daily wall back first |
| Fan curve looks stock after a reset or reboot | not yet verified whether the firmware keeps it | the daily profile writes it again |

## Where things live
- Programs: `%LocalAppData%\Programs\Clocktail` (server, service); the running service: `C:\Program Files\Clocktail`, scheduled task `ClocktailBroker`.
- Per-card state: `%LocalAppData%\Clocktail\<gpu-id>\`: `profile.json` (daily profile), `tuning-log.md` (every change, readable), `datapoints.jsonl`, `ownership.json` (the original value of every setting Clocktail has written), `crashes.jsonl`; `live\` (each live watch: a `.log` of its lines and a `.csv` of every sample, 4 a second); `residency\` (where the card ran under load, once a second, whenever the server is up).
- Service log: `%LocalAppData%\Clocktail\broker.log`.

## Stock and back
- `stock`: the card to stock now and kept there (the daily profile is suspended, not deleted). `load_daily` puts the kept profile back.
- `back_to_daily` puts the daily profile on, not stock; while suspended it leaves the card at stock. A fresh install with no daily profile is stock.
- A reboot alone returns the card to stock until the service writes the daily profile again at logon.
- To remove Clocktail entirely: `npx -y clocktail uninstall` (card to stock, service, files and agent configs removed; the logs stay unless `--purge`).

## Never
- Never work around the service (other overclocking tools writing at the same time fight over the same settings; ask the person to close MSI Afterburner, NVIDIA App's auto-tune and similar while tuning).
- Never edit the profile or journal files by hand.
- Never promise stability you have not measured.
