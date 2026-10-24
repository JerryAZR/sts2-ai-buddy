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

## Fresh-machine setup

1. Install Slay the Spire 2 (Steam). The build treats the game install as the
   SDK; standard path auto-detected, else pass `-p:GameDir=...`.
2. .NET 9 SDK (pinned by `global.json`).
3. `dotnet build` — Debug builds also deploy to `<GameDir>\mods\AiBuddy`.
4. `dotnet test tests/AiBuddy.Tests` — unit + IL-scan structural tests.
5. (Optional, for source diving) regenerate the decompile:
   `ilspycmd "<GameDir>\data_sts2_windows_x86_64\sts2.dll" -o refs/decompiled/sts2-v0.111.0`
   (gitignored reference material; tests don't need it, humans/agents do).

## Headless test harness

```bash
AIBUDDY_AUTO_PHANTOMS=2 AIBUDDY_AUTOPLAY_LOCAL=1 \
  AIBUDDY_TEST_MAP="monster,rest,treasure,event,shop,monster" \
  "<GameDir>\SlayTheSpire2.exe" --force-steam off --fastmp host_standard \
  > build/logs/run.log 2>&1 &
# poll for "Embarking", wait, then: taskkill //IM SlayTheSpire2.exe
```

Env vars (all testbench, never shipped behavior): `AIBUDDY_AUTO_PHANTOMS=N`,
`AIBUDDY_AUTOPLAY_LOCAL=1`, `AIBUDDY_TEST_MAP="monster,rest,treasure,event,shop,…"`,
`AIBUDDY_TEST_EVENTS="PUNCH_OFF,…"` (splice any ModelDb event into the act pool),
`AIBUDDY_TEST_NEOW_RELICS=1`, `AIBUDDY_TEST_COMBAT_CHOICE=1`,
`AIBUDDY_TEST_REST_EXHAUSTION=1`, `AIBUDDY_AUTO_ACT_ON_HOST_END=1`.

Logs: `%APPDATA%\sts2-ai-buddy\logs\session-*.jsonl` (event stream),
`build/logs/` (harness stdout). Two-instance vanilla-guest test: `docs/m7-test.md`.

## Working agreement

Design-first: architecture/algorithm decisions are discussed and approved before
implementation (see `docs/design-v1.md`); testbench stays behind env vars;
claims about game behavior are pinned by tests or live repros, not hand-audits.
