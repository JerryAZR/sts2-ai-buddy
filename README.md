# AI Buddy — Slay the Spire 2 co-op mod

Host-only mod that fabricates "phantom" AI companion players. Phantoms are full
lobby members on every peer: vanilla (unmodded) guests see them as ordinary
players. The brain contract is the seam for a future real brain (heuristic/LLM):
brains decide from detached plain-data views and emit id-based intents; the
control layer owns all engine contact and validates everything.

**Design doc:** `docs/design-v1.md` · **Live-guest test procedure:** `docs/m7-test.md`
**Feasibility report:** `docs/Slay the Spire 2 AI Companion Mod — Feasibility Report.md`

## Status (as of commit e3b2feb/0ccb9e7)

Working and soak-verified headlessly with 2 phantoms + autopilot host, and
verified with a real vanilla guest over ENet (lobby + in-run, no visible flaws):

- Lobby join, ready, embark; guests see phantoms as ordinary players (mod-compat
  passes via manifest `affects_gameplay: false`).
- Neow, map votes (phantom vote gate: phantoms pace themselves via the unanimous
  vote — see design doc §3.5), combats (plan-then-drain executor, kill-chain,
  settle watchdog, choice-resume), rewards, rest sites, shops (full purchases),
  treasure rooms (chest + dedup'd relic votes), normal + **shared** events
  (incl. fight-spawning ones).
- Mechanical coverage pins: every `WaitForRemoteChoice` site has a capture patch
  (IL scan); zero blocking primitives in all 68 events + all cards (IL scan).

**Next up (ordered):**
1. Shop purchase forging (`SyncLocalGoldLost`/`RewardObtainedMessage`/
   `CardRemovedMessage`/`MerchantCardRemovalMessage` stamp the host — phantom
   buys desync guests; avoid shops in live-guest tests until fixed).
2. M6 save/resume (phantoms across save → quit → resume).
3. Crystal Sphere (act-3 bespoke minigame; deferred).
4. M8 polish: character picker (Ironclad hardcoded), display names, chat narration.

**Notes:**
- During co-op combat you may see the game log `[WARN] Attempted to play card …
  no target was passed` + `[ERROR] GameAction … finished … but was in state
  Canceled!`. This is **expected**, not a bug: a phantom's planned target died
  mid-batch (e.g. a faster ally killed it), the game cancels the play
  (`GetCreatureAsync` → null → `IsValidTarget(null)` false → `Cancel()`), and the
  mod's kill-chain aborts the rest of the batch and replans — by design
  (`CombatDriver`). The ERROR is just the game's noisy logging for a routine cancel.

## Fresh-machine setup

1. Install Slay the Spire 2 (Steam). The build treats the game install as the
   SDK; standard paths auto-detected (Windows Steam default and Linux
   `~/.local/share/Steam/...`), else pass `-p:GameDir=...`. The game data dir
   (`data_sts2_windows_x86_64` / `data_sts2_linuxbsd_x86_64`) is picked by existence.
2. .NET 9 SDK (`global.json` pins the band; `rollForward: latestFeature`).
3. `dotnet build` — Debug builds also deploy to `<GameDir>/mods/AiBuddy`.
4. `dotnet test tests/AiBuddy.Tests` — unit + IL-scan structural tests.
5. (Optional, for source diving) regenerate the decompile:
   `ilspycmd "<GameDir>/data_sts2_*/sts2.dll" -o refs/decompiled/sts2-v0.111.0`
   (gitignored reference material; tests don't need it, humans/agents do).
   Linux note: the dnf `ilspycmd` package is broken — install a pinned version and
   roll forward to the .NET 9 runtime:
   `dotnet tool install -g ilspycmd --version 9.1.0.7988` and run with
   `DOTNET_ROLL_FORWARD=LatestMajor`.

## Headless test harness

Windows:
```cmd
set AIBUDDY_AUTO_PHANTOMS=2
set AIBUDDY_AUTOPLAY_LOCAL=1
set AIBUDDY_TEST_MAP=monster,rest,treasure,event,shop,monster
"<GameDir>\SlayTheSpire2.exe" --force-steam off --fastmp host_standard > build\logs\run.log 2>&1
:: poll for "Embarking", wait, then: taskkill //IM SlayTheSpire2.exe
```

Linux (native `SlayTheSpire2` binary, no `.exe`; `pkill` instead of `taskkill`):
```bash
AIBUDDY_AUTO_PHANTOMS=2 AIBUDDY_AUTOPLAY_LOCAL=1 \
  AIBUDDY_TEST_MAP="monster,rest,treasure,event,shop,monster" \
  "<GameDir>/SlayTheSpire2" --force-steam off --fastmp host_standard \
  > build/logs/run.log 2>&1 &
# poll for "Embarking", wait, then: pkill -f SlayTheSpire2
```

### First-run onboarding (fresh profile gotchas)

`--force-steam off` uses an **isolated profile** (`user://default/1`, on Linux
`~/.local/share/SlayTheSpire2/default/1/`) — separate from the Steam profile, so
a modded normal install doesn't leak in. But that profile starts at day zero, and
a fresh profile changes the run the mod sees. Four gates, all traced to decompiled
`sts2.dll` v0.111.0 (`refs/decompiled/`):

1. **Mods won't load** — log shows `Skipping loading mod …, user has not yet
   seen the mods warning`. `ModManager.TryLoadMod` gates on
   `PlayerAgreedToModLoading` (serialized as `settings.save →
   mod_settings.mods_enabled`), set **only** by the in-game dialog — and clicking
   "Load mods" **quits the game** (`NConfirmModLoadingPopup.OnYesButtonPressed` →
   `NGame.Instance.Quit()`). The mod loads on the *next* launch.
   **Fix (one-time):** run the game in a GUI once, click "Load mods", let it quit.
2. **No Neow** — `ActModel.GetUnlockedAncients` removes Neow unless `NEOW_EPOCH` is
   `Revealed`; normally revealed by the Timeline screen tutorial.
3. **Fixed first-run map** — `ApplyActDiscoveryOrderModifications` logs `"First run
   ever. Presenting rooms in a set order."` and forces a curated room sequence when
   `NumberOfRuns == 0` (`= Wins+Losses`, a `[JsonIgnore]` sum over `character_stats`).
4. **FTUE popups** mid-run — `ftue_completed` empty on a fresh profile.

Gates 2–4 live in `progress.save` and are seedable. Run once before the first
headless session (idempotent, backs up to `progress.save.bak`, edits both
`profile1` and `modded/profile1`):

```bash
python3 tools/seed-fresh-profile.py
```

It reveals `NEOW_EPOCH` (plus all 57 epoch slots as `not_obtained`, avoiding
`Epoch X slot missing but parent NEOW_EPOCH is Revealed` warnings), bumps Ironclad
`total_losses` to 1 (so `NumberOfRuns > 0`), and fills `ftue_completed` with the
14 tutorial FTUE ids.

### Environment & logs

Env vars (all testbench, never shipped behavior): `AIBUDDY_AUTO_PHANTOMS=N`,
`AIBUDDY_AUTOPLAY_LOCAL=1`, `AIBUDDY_TEST_MAP="monster,rest,treasure,event,shop,…"`,
`AIBUDDY_TEST_EVENTS="PUNCH_OFF,…"` (splice any ModelDb event into the act pool),
`AIBUDDY_TEST_NEOW_RELICS=1`, `AIBUDDY_TEST_COMBAT_CHOICE=1`,
`AIBUDDY_TEST_REST_EXHAUSTION=1`, `AIBUDDY_AUTO_ACT_ON_HOST_END=1`.

Logs: Windows `%APPDATA%\sts2-ai-buddy\logs\session-*.jsonl`, Linux
`~/.config/sts2-ai-buddy/logs/session-*.jsonl` (event stream); `build/logs/`
(harness stdout). Two-instance vanilla-guest test: `docs/m7-test.md`.

## Working agreement

Design-first: architecture/algorithm decisions are discussed and approved before
implementation (see `docs/design-v1.md`); testbench stays behind env vars;
claims about game behavior are pinned by tests or live repros, not hand-audits.
