# Clocktail

English | [简体中文](README.zh-CN.md)

**Agent-assisted overclocking for NVIDIA RTX 50 (Blackwell), served over MCP.** You play games. An AI agent tunes your graphics card. NVIDIA's own stress tester decides who was right. This is the correct division of labor.

## Congratulations

You have chosen to overclock a graphics card that was already very fast. We respect this decision and will not ask you to explain it.

### Set up Clocktail

Copy this into your coding agent:

```
Install Clocktail and connect it to this coding agent using npx clocktail@latest setup. Show me the setup plan for approval, then verify the installation.
```

Or run it in a terminal yourself (Node.js required):

```
npx -y clocktail setup
```

Either way you choose your agents, review the proposed changes, and approve them. Setup installs Clocktail, connects your agents (with backups of their configuration), and installs the service; Windows asks for admin rights once. Restart your agent afterward. Your card stays at stock until you say otherwise.

Updates do not install themselves. When a new version is out, "How's my card doing?" says so in its last row; close your agents and run the same `npx -y clocktail@latest setup` again. Your card, your daily profile and your saved profiles stay as they are. To check, Clocktail reads one thing from the npm registry, the latest version number, and sends nothing about your computer or card. `CLOCKTAIL_NO_UPDATE_CHECK=1` turns it off.

Setup supports Claude Code, Claude Desktop, Codex, Cursor, OpenCode, VS Code, Gemini CLI, Windsurf, GitHub Copilot CLI and a dozen more (the list is borrowed from [REA](https://github.com/morluto/rea), with thanks). Any other MCP client works too: point it at `%LocalAppData%\Programs\Clocktail\clocktail-mcp.exe`.

### Ask your agent

```
Let's tune my GPU. Start from the founders preset, stress test it, then find me more.
```

```
I'm at 2970 MHz and it won't go higher no matter what voltage. What's holding it back, and what would you change?
```

```
How's my card doing?
```

```
I'm playing Cyberpunk tonight on the new curve. Watch the card the whole time. If it crashes, write it down and tune from it.
```

Ask how your card is doing and you get every setting, original → now, with a ✅ where it sits inside NVIDIA's own limits and a ⚠️ where it does not. Your agent comes with a manual on Blackwell (rails, clock domains, the V/F curve, what limits what) and on the method, written from four days of measurements, so it can explain every step and does not have to guess. While you play it watches the card four times a second and tells you what it sees as it happens. If the game crashes, that is not a setback but a measurement: the agent records the clock, voltage, power and temperature it fell over at, your daily profile goes back on, and the next step lowers that part of the curve. There is no other window to open. Most people find this arrangement agreeable.

## Bottom line

- **The agent does the tuning.** Power limit, fan curve, V/F curve, memory, MSVDD and XBAR, in that order, each for a reason it can explain. One small step at a time, because large steps are how graphics cards end up on the secondhand market.
- **NVIDIA's stress tester is the judge.** `nvgwls.exe` ships with your driver; NVIDIA App's auto-tune runs the same program. It checks every graphics, compute and ray-tracing result bit for bit. It has no feelings to hurt and cannot be bribed.
- **Game-stable = stress-stable minus 30–60 MHz.** The stress tester maps the shape of the edge, voltage by voltage. Only games can say how far under it they need to stay: in our runs, 30–60 MHz under the highest clock `nvgwls` passed. So you play, the agent watches, and every crash goes into the data. We did not make this rule. We merely crashed into it.
- **Keep as many profiles as you like.** Save the daily one as "quiet" or "games" and switch with a sentence. The card only ever runs one at a time, which is how it prefers it.
- **Nothing is permanent unless you say so.** Every write is read back and can be undone. A background service keeps your daily profile and puts it back after every boot and every driver reset, including the ones you caused.

## What it found on the one card we tested

RTX 5090 Founders Edition, driver 617.42, Windows 11. Your card is different. Your card is always different.

| Setting | Result |
|---|---|
| MSVDD REL offset −50 → 0 mV (NVIDIA's own reliability limit) | Blender Cycles +2 %. Free, which is the best price. |
| XBAR clock +300 MHz, XBAR voltage demand +20 mV | Blender Cycles +6–7 % over stock. Memory bandwidth unchanged. |
| XBAR past ~+300 MHz without the extra voltage | MSVDD drops to its floor and memory-bound work loses up to 16 %. Do not do this. We did it so you don't have to. |

Under load, XBAR is capped at about 0.9 × the core clock. Offsets past ~+300 MHz therefore buy nothing. Higher numbers are not always better numbers. This may be the most important sentence in this document.

All of it is in the founders preset.

## Uninstall

```
npx -y clocktail uninstall
```

The card goes back to stock, and the service, the files and the agent connections are removed. Your profiles and tuning logs stay in `%LocalAppData%\Clocktail` unless you add `--purge`. The card will not remember any of this.

## What is in the box

| Folder | Contents |
|---|---|
| `src/Clocktail.Hw` | The only code that talks to the driver (NVAPI, NVML). Every setter has a write policy, because trust is earned. |
| `src/Clocktail.Domain` | Transactions, the ownership journal (the original value of every setting it has touched), write rules, the daily profile, presets. |
| `src/Clocktail.Broker` | The service: an elevated logon task that does every write over a named pipe and restores your settings after crashes and driver resets, without comment. |
| `src/Clocktail.Cli` | Command line, for people who prefer to break things by hand. |
| `tools/Clocktail.Mcp` | The MCP server your agent talks to. See its README. |
| `npm/` | The setup genie behind `npx clocktail`. |

Build from source (Windows, .NET 10 SDK): `dotnet test Clocktail.slnx` runs the tests on a fake driver; it never touches the GPU.

## Sources

The NVAPI interface details come from public sources on the internet. Every value the code relies on was verified by measurement on the card above. Some of those measurements involved driver resets. The card is fine.

## Warning

This is unofficial software. Overclocking can crash your games, corrupt your output and shorten the life of your hardware. You use it at your own risk. If something goes wrong, the tuning log will tell you exactly what happened. It will not tell you it's sorry.
