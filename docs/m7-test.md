# M7 Steam/ENet Go/No-Go Test — Vanilla Guest Procedure

Status: **lobby phase verified** (2026-10-24, two local instances). The vanilla guest
connects to the modded host, passes the mod-compat check ("Non-gameplay relevant
mods... AiBuddy"), and sees **Players: 4** (host + guest + 2 phantoms) — phantoms
propagate as ordinary lobby members.

## Verified automatically

- Guest joins modded host (`--nomods` guest ↔ modded host): no version/mod errors.
- Lobby list on the guest includes phantoms 1001/1002 as ordinary players.
- No errors on either side through lobby join + character pick.

## What needs a human (you)

The vanilla guest can't drive itself (`--autoslay` is disabled in release builds).
You play the guest; the host plays itself (autopilot).

### 1. Launch the host (modded, self-driving)

```bash
cd C:/Users/Jerry/Projects/Mods/sts2-ai-buddy
AIBUDDY_AUTO_PHANTOMS=2 AIBUDDY_AUTOPLAY_LOCAL=1 \
  "C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.exe" \
  --force-steam off --fastmp host_standard > build/logs/m7-host.log 2>&1 &
```

Wait ~30s for the lobby (char select) to be up.

### 2. Launch the guest (vanilla)

```bash
"C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.exe" \
  --force-steam off --nomods --fastmp join --clientId 2 > build/logs/m7-guest.log 2>&1 &
```

### 3. On the GUEST window

- Pick a character, click **Ready**. The host embarks when everyone is ready.
- Then just play normally: Neow, map votes, combat, rewards.
- Watch: phantoms 1001/1002 should look like normal players — they pick Neow
  options, vote, take combat turns after the host, collect rewards.

### 4. What to check after

- **Guest log** (`build/logs/m7-guest.log` + `%APPDATA%/SlayTheSpire2/logs/`):
  any "state diverged" / checksum errors = FAIL (name the action).
  Hangs = last `WAITING`-ish line names the missing fabrication.
- **Host event log** (newest `%APPDATA%\sts2-ai-buddy\logs\session-*.jsonl`):
  `forged_broadcast` events show every phantom → guest message.

## Known gaps / expected issues

- **Shop purchases by phantoms don't reach guests** (`SyncLocal*` stamps the host,
  not the phantom). Phantom shop buys will desync the guest — AVOID shops with a
  live guest, or expect the diverge. Forging for purchases is TODO.
- Shared/collaborative events (M4) — phantoms abstain; may stall if one appears.
- Treasure rooms: FIXED (chest + dedup'd relic votes + input-state mirror).
  Residual: if a human ties a phantom's relic vote in the same instant, an RPS
  relic fight starts — auto-resolved by RNG model-side, but the award animation
  may log an exception. Harmless.
- Guest "Invalid Task ID" errors at join: observed, appears benign.

## Kill switch

`taskkill //IM SlayTheSpire2.exe` (both instances share the process name).
