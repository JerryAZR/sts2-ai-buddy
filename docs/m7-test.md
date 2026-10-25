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
cd ~/Projects/mods/sts2-ai-buddy
G="$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2"
AIBUDDY_AUTO_PHANTOMS=2 AIBUDDY_AUTOPLAY_LOCAL=1 \
  "$G/SlayTheSpire2" \
  --force-steam off --fastmp host_standard > build/logs/m7-host.log 2>&1 &
```

Wait ~30s for the lobby (char select) to be up.

### 2. Launch the guest (vanilla)

```bash
G="$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2"
"$G/SlayTheSpire2" \
  --force-steam off --nomods --fastmp join --clientId 2 > build/logs/m7-guest.log 2>&1 &
```

### 3. On the GUEST window

- Pick a character, click **Ready**. The host embarks when everyone is ready.
- Then just play normally: Neow, map votes, combat, rewards.
- Watch: phantoms 1001/1002 should look like normal players — they pick Neow
  options, vote, take combat turns after the host, collect rewards.

### 4. What to check after

- **Guest log** (`build/logs/m7-guest.log` + `~/.local/share/SlayTheSpire2/logs/`):
  any "state diverged" / checksum errors = FAIL (name the action).
  Hangs = last `WAITING`-ish line names the missing fabrication.
- **Host event log** (newest `~/.config/sts2-ai-buddy/logs/session-*.jsonl`):
  `forged_broadcast` events show every phantom → guest message.

## Known gaps / expected issues

- **Shop purchases by phantoms: forging implemented** (gold/reward/removal
  messages re-broadcast with the phantom as wire sender; removal rides the
  deck-choice fabrication). Host-side soak-verified, NOT yet confirmed with a
  live guest — include a shop visit in the e2e and check the guest's phantom
  gold/deck/relics (not the host's) plus checksums. Nuance: phantom card-removal
  skips `AfterItemPurchased`/`PurchaseCompleted` hooks (local-only, sim-safe for
  guests — but a purchase-hook relic won't see phantom removals).
- Shared/collaborative events: SUPPORTED (phantoms vote via fabricated
  `VotedForSharedEventOptionMessage`; host tallies as usual). Event-spawned
  fights covered by the combat driver.
- Treasure rooms: FIXED (chest + dedup'd relic votes + input-state mirror).
  Residual: if a human ties a phantom's relic vote in the same instant, an RPS
  relic fight starts — auto-resolved by RNG model-side, but the award animation
  may log an exception. Harmless.
- Guest "Invalid Task ID" errors at join: observed, appears benign.

## Kill switch

`pkill -f SlayTheSpire2` (both instances share the process name).
