# AI Buddy — v1 Design Doc

Status: **DRAFT — awaiting approval.** No implementation until approved (per project AGENTS.md).

Scope: host-only StS2 mod; guests run vanilla. Multiple AI companions ("phantoms") are a first-class, tested-from-day-one invariant (§2). Trivial/random policy only in v1.

---

## 1. Core architecture: the phantom is a virtual peer; the mod is its network stack

StS2's simulation is deterministic and command-based: peers exchange *actions*, never state, and checksum after every action. The host's broadcast relay preserves the original sender id (`NetHostGameService` reserializes with `overrideSenderId`). A lobby member is nothing but a `ulong NetId` plus a stream of well-formed messages.

Therefore the phantom is modeled as a **full lobby member on every peer** (guests see a completely ordinary player), and the mod fabricates everything that peer would produce:

- **Host-local injection** into host-side synchronizers (`CombatStateSynchronizer`, `MapSelectionSynchronizer`, `ActChangeSynchronizer`, `PlayerChoiceSynchronizer`, ready sets in `CombatManager`) — the values the phantom "sent".
- **Forged-sender broadcasts to guests** for everything guests' sims await from the phantom: `SyncPlayerDataMessage`, `PlayerChoiceMessage`, votes, and `ActionEnqueuedMessage`-bound actions (which carry an explicit `playerId`/`OwnerId` and flow through the normal host broadcast).

**Hard rules** (from the determinism model — violating these kicks guests with "state diverged"):

0. **The phantom has no client.** Anywhere the game gates on `LocalContext.IsMe(player)` — reward offers (`RewardsSet.Offer` no-ops for non-local players), card-select screens, obtain animations — that code runs on *no* peer for the phantom. The mod acts as the phantom's client: read the deterministic state host-side, decide, and emit the messages the owning client would have sent.

1. The mod **never mutates deterministic sim state directly** on any peer's behalf except by issuing the same vanilla commands/actions a real client could issue.
2. The mod **never alters what executes or in what order** on the host — no patches inside deterministic execution paths, only message fabrication and host-local UI.
3. The mod **fabricates messages only for phantoms, never for real clients.** Discriminator: a NetId is live iff it appears in the transport's `ConnectedPeers` (KitLib's `IsLiveEnetPeer` pattern — NetId-range checks are the bug they had to fix).
4. The mod **registers no content** (nothing in `ModelDb`) and **no custom `INetMessage` types** in v1, so the consistency hash and message-type table stay byte-identical to vanilla.
5. Manifest sets `affects_gameplay: false` (de-facto semantics: "alters content/serialization", which we do not) so vanilla guests get at most a non-gameplay-mismatch warning, never a block.

## 2. Phantom identity

- Each phantom has a **stable `ulong NetId`** from a configurable range, default base **1001**, allocated sequentially per added phantom (1001, 1002, …). Real SteamID64s are 17 digits; collision on Steam is impossible. The `slotId` field is 2-bit (max 4 slots total, humans + phantoms) — enforced by the vanilla lobby, and respected by the add-flow (button no-ops when the lobby is full).
- **N-phantom invariant:** there is no "the phantom" anywhere in the design. Every subsystem (registry, lobby fabrication, synchronizer drivers, control layer, policies, observability tags) iterates a *collection* of phantoms. Integration tests run with **≥2 phantoms from day one** so single-phantom assumptions surface immediately rather than after they're load-bearing.
- The NetId is **persisted per save** (it lives in `SerializablePlayer.net_id` automatically once the phantom is a run player) and must be re-fabricated identically on resume.
- `SerializableUnlockState`: phantom copies the host's unlock state at run creation, so unlock-filtered content generation matches.
- **Display name**: vanilla guests resolve names via `SteamFriends.GetFriendPersonaName(netId)`, which fails for phantom ids and falls back to printing the raw number ("1001"). **Accepted for v1.** Host-side only, we patch name resolution to show a configurable display name (default "Buddy").
- `isModded` flag on the phantom's lobby struct mirrors the host's value.

## 3. Component map

All integration points below were verified against decompiled `sts2.dll` v0.111.0 (see `refs/decompiled/sts2-v0.111.0/`).

### 3.0 Observability layer (built FIRST, before any phantom logic)

Assume it breaks; instrument first. Five interleaved log sources, one timeline:

1. **Wire tap** — postfix patches on `NetMessageBus.SerializeMessage` (outbound) and inbound dispatch: every message with seq#, direction, type, sender NetId, payload summary. Pure observation. The message stream is the ground truth of a deterministic run.
2. **Wait tracer** — patches at every point the game starts/stops waiting on a player (synchronizer wait begin/resolve, `SetReadyToEndTurn`/`SetReadyToBeginEnemyTurn`, vote handlers, choice reserve/resolve), logged as `WAITING { system, on: [ids] }` / `RESOLVED { system, by: id }`. A hang's last WAITING line names the culprit system and player.
3. **Action queue trace** — enqueue/execute with action id, owner id, type; verifies phantom actions carry the right `OwnerId`.
4. **Checksum log** — every `ChecksumTracker.GenerateChecksum(context, action)` with its id, so a divergence can be bisected to the exact action. Checksums stay **enabled** in all testing (never patched off).
5. **Mod decision log** — every fabrication (injected sync, forged message, mirrored vote), every brain proposal, every validation rejection.

Tooling: JSONL file per session (seq + game tick + per-phantom tags interleave all five sources); **panic-dump hotkey** serializing every synchronizer's current wait state + queue contents; host-only overlay showing live "waiting on" state; game's own `CombatReplayWriter` divergence replays kept on. Determinism is our debugger: seed + event stream = repro. All observability patches are compiled behind a debug flag, off in release builds.

### 3.1 PhantomRegistry (mod-internal)

Owns the set of phantom NetIds for the current session, their characters, display names, and policy instances. Answers the one question every patch asks: *"is this NetId a phantom?"* — registry membership **and** not in `ConnectedPeers`.

### 3.2 Lobby fabrication (new run)

**Join flow (v1, concrete):** on the vanilla multiplayer character-select screen (`NCharacterSelectScreen`, host side), the mod adds a host-only **"Add AI Buddy" button** (code-built Godot controls; asset-free, per Modding-Tutorial ch. 14). Each click:

1. No-ops if the lobby is full (4 slots total, humans + phantoms + pending joiners).
2. Allocates the next phantom NetId (base + count).
3. Creates `StartRunLobbyPlayer { id = phantomId, slotId, character = Ironclad, unlockState = host's, isModded = host's, isReady = true }` — **character is hardcoded to Ironclad in v1** (the locked "host chooses character" decision is deferred to a picker in M8 polish).
4. Injects it via the same `TryAddPlayerInFirstAvailableSlot` path real joins use (or direct list insertion if that path proves join-handshake-coupled), then broadcasts a forged `PlayerJoinedMessage { lobbyPlayer }` so guests build the identical list.

Phantoms join **already ready**; the run begins when the human players ready up, exactly like vanilla. v1 has no lobby remove button (leave/rehost to reset) — noted as a convenience gap, not an architecture gap.

Run start proceeds normally: `LobbyBeginRunMessage.playersInLobby` (host-controlled) carries all phantoms to every guest, and `RunState.CreateForNewRun` constructs their `Player`s everywhere identically.

**Resume flow:** covered by §3.6 — phantoms rejoin automatically, no button.

### 3.3 RunLobby registration

Phantom is added to `RunLobby.Players` on **host and implicitly on guests** (guests build `RunLobby` from the lobby player list). Consequence: guests' synchronizers will await the phantom — by design, and §3.4 satisfies them. The phantom never connects at transport level, so it never appears in `ConnectedPeers` and never receives heartbeats (client↔host only; harmless).

### 3.4 Synchronizer drivers (the "network stack")

The complete inventory of what the game awaits from each player (all 13 `RunManager` synchronizers audited, v0.111.0). Every "yes" reduces to the same two primitives: **host-local injection + forged-sender broadcast**.

| Synchronizer | Awaits phantom? | What it wants |
|---|---|---|
| `CombatStateSynchronizer` | Yes | `SyncPlayerDataMessage` per room transition (host builds `SerializablePlayer` from the phantom's real host-side `Player`; inject into `_syncData` + forged broadcast) |
| `ActionQueueSynchronizer` | Yes | All combat actions; host enqueues with phantom `OwnerId` via a `RequestEnqueue` prefix calling `EnqueueAction(action, action.OwnerId)` directly (KitLib `HostEnqueuePatch` pattern) |
| `PlayerChoiceSynchronizer` | Yes | Mid-action choices: fabricated `PlayerChoiceMessage { choiceId, result }` + resolve host-side waiter |
| `MapSelectionSynchronizer` | Yes | Map votes — v1: **mirror host player's vote** (locked decision; abstention risks stall) |
| `ActChangeSynchronizer` | Yes | Next-act votes — mirror host |
| `EventSynchronizer` | Yes | `OptionIndexChosenMessage` for personal events; `VotedForSharedEventOptionMessage` for shared/collaboration events (mirror host) |
| `RewardsSetSynchronizer` | Yes | Combat-reward selection or skip per player |
| `RewardSynchronizer` | Yes | `RewardObtainedMessage` / gold / card-removal notifications |
| `RestSiteSynchronizer` | Yes | Rest option chosen / skipped (hover is cosmetic) |
| `TreasureRoomRelicSynchronizer` | Yes | Relic picks/votes |
| `OneOffSynchronizer` | Yes | Merchant card removal, treasure chest, Crystal Sphere rewards |
| `FlavorSynchronizer` | No | Pings/emotes — cosmetic |
| `PeerInputSynchronizer` | No | Input-state display — cosmetic |

Plus lobby ready states, `CombatManager`'s two ready sets, and checksums (client→host; the phantom has no separate sim, so **no checksum fabrication is needed or possible**).

**Card-select prompts** ("pick a card to remove", "choose a card") are a distinct mechanism: resolved host-side for the phantom via the `CardSelectCmd.UseSelector` / `ICardSelector` seam (care: global static stack — scope installs tightly) or by driving the native screen; the result propagates via the normal choice/action messages.

**Crystal Sphere is NOT a special protocol case**: the minigame is fully local per player (grid + placement RNG live on the owning client only); the sole wire artifact is the final `CrystalSphereRewardsMessage` via `OneOffSynchronizer.DoLocalCrystalSphereRewards`. Phantom handling = play/solve the grid host-locally with the game's own `CrystalSphereMinigame` class; outcome flows through the standard fabrication path. Deferred anyway (act-3-only; see milestones).

### 3.5 Control layer (host-gated, owns all mechanical confirmations)

Per the locked decisions:

- **Mechanical confirmations are never the brain's job.** The control layer auto-issues: end-turn ready when the phantom has nothing left to do, ready-to-begin-enemy-turn, vote mirroring, room-advance ready-ups. A missed confirmation stalls three humans; a missed card play is a no-op.
- **Combat turns (amended, user-approved):** phantoms hold their end-turn while the local host player is still playing; when the host ends their turn, phantoms take their turns **in registration order** (buddy N+1 waits for buddy N, so each sees the previous one's results), playing one card per tick until the policy finds nothing playable, then end promptly. The host-only **"Buddy: act" button** remains for manual mid-turn triggers; **wake-up** = `NetUndoEndPlayerTurnAction` (vanilla back-out) when the phantom already ended.
- **Brain boundary**: the policy receives a structured snapshot (game state + legal-action list) and returns *candidate* actions. **The control layer validates every candidate against the legal-action list before enqueue** — the brain is untrusted; invalid output is discarded/retried, never executed. (User requirement.)
- v1 policy: random legal card play; abstain-by-readying otherwise.

**Room pacing — the vote gate (user-approved, M7 hardening):** a phantom paces itself through its own map vote. Map movement in vanilla is *unanimous* (`MapSelectionSynchronizer` requires every player's vote before `MoveToMapCoord`), so the phantom casts its vote only when **(a) its brain has given a terminal answer — act or explicit abstain — for every decision flow the current room presented (rewards / shop / rest / event), and (b) the host's vote is known** (mirrored). Whichever condition completes second triggers the vote. Consequences:

- "Done" is a **brain choice**, never inferred from state cleanliness: an abstained card reward legitimately remains on screen while the phantom is done. Brain answers are conceptually tri-state (act / abstain / undecided); the synchronous trivial brain never says "undecided", but the distinction becomes load-bearing for a real async (LLM) brain — a slow brain simply makes the party wait at the map, exactly like a slow human. No host politeness is assumed or required.
- **Boundary hygiene (cleanup, never pacing):** vanilla force-skips the *local* player's leftover reward sets at room exit (`BeforeLeavingRoom`); phantoms have no "local", so the control layer mirrors that step for phantom leftover sets at the same vanilla moment, forged to guests. Without it, abstained sets would leak open on every peer for the rest of the run.

### 3.6 Save / resume

Recon finding: vanilla `RunManager.CanonicalizeSave(save, localPlayerId)` only validates that the **host's own** id is in the save; the phantom's entry rides through harmlessly, and `SerializablePlayer` stores only `net_id` (no Steam validation). Design:

- On load, the mod hooks the multiplayer load flow (`NMultiplayerSubmenu.StartLoad` / `LoadRunLobby` creation), identifies the run's phantoms (saved `net_id`s inside the configured phantom range — SteamID64s can't collide — cross-checked against the mod's own per-save metadata when present), and **re-injects each phantom into `LoadRunLobby.Players`** (ids from the save, `isReady = true`) **before** `ShouldAllowRunToBegin()` compares counts — so the "missing players" popup never appears (user's suggestion; no suppression needed).
- `RunManager.SetUpSavedMultiplayer` then copies it into the new `RunLobby`, restoring the same uniform architecture as a fresh run.
- Defensive prefix on `RunSaveManager.RenameBrokenMultiplayerRunSave` (and the catch path in `LoadAndCanonicalizeMultiplayerRunSave`) so a phantom-related validation surprise can never rename the save `.corrupt` mid-campaign. Worst case: log + error surface to host.
- The phantom obviously never sends `ClientLoadJoinRequestMessage`; nothing awaits it at the wire level.

### 3.7 Defensive guards (crash landmines found in recon)

- **Targeted sends to a phantom id throw** (`SteamHost.SendMessageToClient` → `InvalidOperationException`). Prefix-patch the host send path: if target is a phantom id, silently drop (return success). Broadcasts never touch this path.
- `NetHostGameService.GetVersionInfoForPeer(phantomId)` returns null and some join paths call `.Value` — only reachable if a phantom "connects", which never happens; guard anyway if the audit finds in-run callers.
- Audit during implementation: any host code iterating `RunLobby.Players` and sending *targeted* messages.

## 4. Bring-up plan (milestones)

Ordered by **gameplay order** (user decision): each milestone extends how far a run can proceed with a phantom participating. Generic seams are built fully; per-content special cases only where forced.

| Milestone | Content | Exit criterion |
|---|---|---|
| **M0 — skeleton + observability** | Mod loads (`[ModInitializer]`), manifest `affects_gameplay: false`, pinned net9.0 build; full §3.0 observability layer | Mod listed in-game; wire tap + wait tracer produce a clean timeline of an unmodded solo run |
| **M1 — lobby join & run start** (ENet loopback) | PhantomRegistry (N-capable); "Add AI Buddy" button (Ironclad hardcoded); lobby injection + forged `PlayerJoinedMessage`; ready handling; run begins with phantoms in `RunState`/`RunLobby` everywhere | Solo host starts a seeded run with **2 phantoms**; host sim consistent |
| **M2 — Neow / ancient** | Event-option choice via `EventSynchronizer`; relic grants ride the command layer for free (`RelicCmd.Obtain` inside the option's `onChosen` — no reward screen at the protocol level); card-select grids (`ICardSelector` seam — covers `MassiveScroll`/`ScrollBoxes`-style relics). **Subset:** v1 policy avoids options that open reward-set offers (`LostCoffer` — one relic among ~24) until M3 lands | Phantom completes Neow (incl. card-removal and card-grid options) |
| **M3 — map + combat + rewards** | Map vote mirroring; `CombatStateSynchronizer` sync fabrication; action enqueue with phantom `OwnerId`; host-gated combat button + wake-up (`NetUndoEndPlayerTurnAction`); auto-ready driving; reward collection in fixed order **relic → gold → cards** (relic/gold unconditional; card reward = pick-or-skip via validated policy) | Phantom survives combats, takes rewards; button gating and wake-up verified |
| **M4 — events** | Personal event options (full — same seam as Neow); shared-event vote mirroring; conservative default (first non-lethal option) + loud log for unrecognized custom events | Phantom completes common act-1 events |
| **M5 — rest sites + shops** | Rest option chosen/skipped; shop = **no-op initially** (phantom buys nothing, just leaves) | Act 1 completable end-to-end most runs |
| **M6 — save/resume** | §3.6 load-flow injection + corrupt-rename guards | Save → quit → resume mid-act with phantom, 5× |
| **M7 — Steam P2P go/no-go** (needs a friend; only after M1–M6 stable) | Switch transport to Steam; vanilla guest joins | Guest sees phantom as normal player; full room completes; no divergence |
| **M8 — polish** | Host-side display names, character picker (replaces hardcoded Ironclad), chat narration, config UI | — |

M1–M6 are fully host-only (ENet loopback, no second account). Act 2 is expected to reuse the same seams unchanged; act-3 Crystal Sphere is the only known bespoke content and is deferred (§6).

## 5. Testing strategy

- **Unit tests** for pure logic: legal-action enumeration/validation, registry, id allocation, vote-mirror decisions. (Follow the project's tests-quality checklist.)
- **Integration = the game itself.** The ENet loopback harness is the test rig: scripted run segments (combat, event, shop, rest, map vote) driven headlessly where possible (`NSceneBootstrapper`/`--fastmp` patterns as reference).
- **Divergence canary**: during M7+, keep `ChecksumTracker` enabled (never patch it off); log every checksum id so the first divergent action is identifiable.
- Every game patch: re-verify patched signatures against a fresh decompile before releasing (EA maintenance tax).

## 6. Explicit non-goals for v1

- No custom message types, no guest-side mod, no enhanced guest HUD (§4.3 of the feasibility report — presence-gated pattern if ever needed).
- No real AI policy (heuristic/LLM brains plug into the validated control protocol later).
- No Crystal Sphere de-minigaming (the minigame is protocol-trivial — one outcome message — but the local solver + brain UX is real work; act-3-only, so a conservative generic handler covers v1).
- Act-2+ content is expected to ride the same seams; anything that doesn't gets conservative-default + loud-log treatment.
- No >4 player lobbies (requires RMP player-limit mod on *all* clients — incompatible with vanilla guests).
- No mid-campaign phantom add/remove (lobby-only, like vanilla players).

## 7. Top risks

1. **M7 fails** (Steam lobby behaves differently than ENet for fabricated members). Mitigation: fallback Path B from the feasibility report (second local instance) — all control-layer/brain work carries over.
2. **EA patch drift** — every Harmony target above is a re-verification point per game update.
3. **An EA patch adds a 14th synchronizer or changes an awaited message** → hang, not crash. Mitigation: the wait tracer makes unknown waits immediately visible; the §3.4 inventory is complete as of v0.111.0.
