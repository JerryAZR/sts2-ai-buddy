# AI Integration Interface — decisions & pending questions

The brain contract seam (design-v1 §3.5) becomes a pluggable per-phantom brain.
This file tracks the locked decisions and what's still open. Nothing here is
implemented yet; implementation starts only after the list below is fully walked.

## Locked

1. **Process boundary**: agents run **in-process** (mod is self-contained). LLM
   inference is external via the standard OpenAI API; users bring their own keys.
   One `IBuddyBrain` contract; an OpenAI-backed implementation is just another
   in-process brain that happens to do HTTP.

2. **Identity**: **one brain instance per phantom** (personas). Calls never merge
   across phantoms — 2 phantoms = 2× the calls, that's the price of personas.

3. **Context breadth**: more context than the current minimal views is needed;
   *what* goes in is a later dedicated discussion. (Ids-only vs +rules-text etc.)

4. **Step reduction** (walked one by one):
   - **A. Whole-turn combat plans — LOCKED.** `TurnPlan { Actions, EndTurn }`
     replaces `PlayBatch`/`EndTurnIntent`; Actions are cards AND potions (see
     interface shape below). All plays succeed + EndTurn → executor ends turn
     (typical turn = 1 call). No EndTurn → brain re-asked with fresh state.
     Any rejection → kill-chain aborts, EndTurn flag voided, brain re-asked.
     Brain-beware: EndTurn after playing a draw card forfeits the drawn cards.
     Violation handling (user-corrected): a plan whose plays ALL fail validation
     gets a RE-PLAN (the brain sees fresh state; repeated violations are
     unlikely), not a forced end; the zero-progress watchdog is warn-only
     escalation (diagnose, don't panic — a stall shows in the wait tracer).
     Violation feedback is transported as data in the next view (`LastOutcomes`
     with `contract:` reasons); whether it reaches the LLM prompt is the brain
     implementation's choice. Sole exception: `TurnPlan([], EndTurn:false)`
     stays a loud violation + force-end — unlike all-fail, it carries zero new
     information, so a re-ask is provably identical token burn.
   - **B. Whole-event paths — LOCKED, with RNG revealed.** One call per event:
     the brain receives the full page tree INCLUDING RNG outcomes (justification:
     human players can save & load to peek — parity, not cheating). Examples:
     Tablet of Truth previews which cards would be upgraded; the bridge event's
     lose-HP-to-reroll becomes flat enumerated options ("lose N hp, remove card X").
     Divergence handling: control layer validates per page as it arrives; if the
     actual page wasn't in the predicted set, re-ask for the subtree.
     See "Event census" below for the per-event classification.

   - **C. Option + follow-up prompt pairing — LOCKED (generic rules, user).**
     Governing principle (refines B's blanket RNG-reveal): **reveal RNG iff a
     decision in the same call depends on the outcome.** Rules:
     (1) Removal pickers (deck is in context): agent declares the removal target
     UPFRONT with the triggering choice; invalid declarations are rejected and
     the prompt falls through to a live re-ask.
     (2) Upgrade pickers (Smith, "upgrade a card of your choice"): upfront
     declaration. Random-target upgrades with no picker stay random.
     (3) Transforms: agent declares WHICH card upfront; the result is rolled at
     resolution, unseen — no combination matrix (too much info; the result
     affects no further decision).
     (4) Neow: simplify only when convenient — once per run, don't overengineer.
   - **D. Reward-set bundling — LOCKED (user).** One call per drain episode
     covering ALL pending sets (sets don't change mid-collection — no re-ask
     path needed). Collection order stays relic → gold first, UNCONDITIONALLY,
     no brain decision (no downside relics exist; gold is never wrong). Then the
     brain call answers, in one shot: (potions) per offered potion: take or
     leave, and when the belt is full ALSO which belted potion to discard
     (view shows new offers + current belt); (cards) per set: pick one of the
     offered ids or skip (view shows every set's offerings: set 1: A,B,C;
     set 2: D,E,F; ...).
- **E. Standing directives — REJECTED (user).** A–D left no trivial-decision
  residue for a heuristic to absorb, and a second (interpretation) decision-maker
  is an unauditable liability.

## Interface shape (locked in discussion; contract types, not yet implemented)

- **Async pull with pending-decision slots.** Core asks, brain answers when
  ready: per (phantom, usecase) slot on the session holds Task + CTS. Decision
  point reached, slot empty → snapshot view, call brain, stash task. Task
  complete → validate against LIVE state (staleness is normal; validation +
  kill-chain own it) → actuate. Boundary crossed while pending → cancel + drop;
  stale answers never actuate. One outstanding request per (phantom, usecase).
  Core guarantees: immutable snapshot views; feedback as data; cancellation via
  token. Brain guarantees: completes/cancels/throws eventually; never mutates;
  ids only. Pacing: a thinking brain = a thinking human (no vanilla turn timer);
  the vote gate's "undecided" state paces rooms with zero special-casing.
- **(1) Static self-state in EVERY view** (user-locked): deck, potions/belt,
  gold, HP/maxHP, relics, max energy — a `SelfState` record embedded in all
  decision views.
- **(2) TurnPlan supports potions** (user-locked): actions generalize —
  `TurnAction = PlayCard(HandIndex, CardId, TargetCombatId?) | UsePotion(BeltSlot, PotionId, TargetCombatId?)`.
  Potions ride the same action-queue OwnerId seam as cards (no new forge surface).
- **(4) Push channel** (user-locked): brains may push unsolicited intents
  anytime (v1 driver: potions usable outside combat). Core drains per tick,
  validates at actuation. `bool TryTakePushedIntent(out BuddyPushIntent)`.
- **Lifecycle hooks** on the interface (`OnRunStarted` / `OnActStarted` /
  `OnBoundary`) — no-ops for stateless brains, context-management points for
  stateful ones.
- Refactor path: `TrivialBrain : IBuddyBrain` wraps TrivialPolicy with
  `Task.FromResult` — synchronous completion keeps every current test/soak
  behavior bit-for-bit; drivers move usecase by usecase (combat first).

## Event census (for B) — mechanical scan, v0.111.0, all 68 events

Base facts: a "page" is just a `SetEventState(description, options)` call — page
transitions are code paths, deterministic given the chosen option unless the
handler rolls RNG to build the next page. RNG comes in three kinds:
`EventModel.Rng` (per-event, seeded run seed + slot + event id — savescum-stable),
`Owner.PlayerRng.Rewards` (per-player run stream — reward grids), and relic
queue pulls (`RelicGrabBag.PullFromFront` — peekable without consuming).
`CalculateVars()` rolls are baked into option text before options exist (free to
show). Transforms depend on the chosen card (pool + rarity filter from the
original) → pre-rolling a transform = one roll PER candidate card.

**62/68 events: generic whole-path preview works** (many also trigger the
option+prompt pairing of item C — Amalgamator, AromaOfChaos, DoorsOfLightAndDark,
FieldOfManSizedHoles, GraveOfTheForgotten, LuminousChoir, MorphicGrove,
RoomFullOfCheese, SapphireSeed, SelfHelpBook, SpiralingWhirlpool, SpiritGrafter,
StoneOfAllTime, Symbiote, WaterloggedScriptorium, Wellspring, WhisperingHollow,
WoodCarvings, ZenWeaver). Includes DollRoom (pay HP → rolled page 2 — pre-roll
the reveal in the call) and ancients/Neow (offers rolled at generation).

**6 flagged — ALL RULED (user-locked):**
1. **SlipperyBridge** — unbounded HP-cost reroll loop (card-to-lose re-rolled
   per HoldOn, cost escalates, pool shrinks). RULED: pre-roll a window of up to
   **20** outcomes (arbitrary safety cap; the loop is naturally bounded by HP —
   LLMs handle tens of choices fine, no tighter cap). Brain commits to "accept
   the i-th offer" or "leave" in one call.
2. **EndlessConveyor** — same shape with gold. RULED: same windowed pre-roll,
   cap 20, naturally bounded by gold.
3. **TabletOfTruth** — bounded (5) but SEQUENTIAL dependent rolls (each upgrade
   shrinks the pool). RULED: simulate the chain (roll → apply → roll from shrunk
   pool); brain picks a stop depth from the fully pre-rolled chain.
4. **Trial** — defendant rolled at Accept determines page 2. RULED: effectively
   auto-choose Accept: pre-roll + reveal the defendant, and the brain's single
   call picks among that defendant's page-2 choices. DOUBLE_DOWN (abandon-run
   popup) blacklisted, never offered.
5. **TinkerTime** — rolled subsets are 2-of-3 types × 2-of-3 riders per type =
   exactly 4 reachable cards. RULED: flatten — pre-roll, show all 4 cards, brain
   picks one; control maps the card back to the (type, rider) option path.
6. **FakeMerchant** — custom shop layout. RULED: treat as a shop (ShopDriver
   purchase rule on its inventory); the shared-vote phase already works.
   Deferred work item, not blocking B.

## Open questions

- **Brain implementation model** (raised in discussion, deliberately OPEN):
  long-lived agent session per phantom (conversation + compaction + tool calls —
  pi's SDK is the reference shape: session owns conversation/tools/compaction,
  `prompt()` injects messages, intents return via submit-tool/structured output)
  vs stateless Q→A per decision. If long-lived: **survey existing C# agent
  libraries** before building anything bespoke. Either way the mod ships a
  static game-knowledge database (card/relic/potion text by id) usable as
  brain-side tools — never live state.
- Potion addressing in intents: belt-slot + id cross-check (like cards) vs
  id-only (slots shift as potions are used/discarded).
- Push channel v1 scope: out-of-combat potions only, vs leaving the door open
  (chat emotes, map suggestions to the host).
- Push failure policy: illegal-when-drained pushes — silent drop + log, vs
  bounce back to the brain as feedback.
- Context breadth per view beyond SelfState (decision 3 — dedicated discussion).
- Brain construction/config surface: when brains attach (lobby add / resume),
  persona config (name, model, key source, system prompt).
- Failure policy for a brain that errors or times out mid-decision (fall back
  to trivial? park + watchdog? per-usecase?).
- Token/cost telemetry (log per-call token counts into the event log?).
