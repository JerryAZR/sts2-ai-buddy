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
   - **A. Whole-turn combat plans — LOCKED.** `TurnPlan { Plays, EndTurn }`
     replaces `PlayBatch`/`EndTurnIntent`. All plays succeed + EndTurn → executor
     ends turn (typical turn = 1 call). No EndTurn → brain re-asked with fresh
     state (today's behavior). Any rejection → kill-chain aborts, EndTurn flag
     voided, brain re-asked with outcomes. `TurnPlan([], EndTurn:false)` is a
     contract violation: logged loudly, treated as end-turn (nothing can change
     mid-turn without a play — an empty no-end answer is an infinite re-ask loop).
     Brain-beware: EndTurn after playing a draw card forfeits the drawn cards.
   - **B. Whole-event paths — LOCKED, with RNG revealed.** One call per event:
     the brain receives the full page tree INCLUDING RNG outcomes (justification:
     human players can save & load to peek — parity, not cheating). Examples:
     Tablet of Truth previews which cards would be upgraded; the bridge event's
     lose-HP-to-reroll becomes flat enumerated options ("lose N hp, remove card X").
     Divergence handling: control layer validates per page as it arrives; if the
     actual page wasn't in the predicted set, re-ask for the subtree.
     See "Event census" below for the per-event classification.

## Pending (walk one by one)

- **C. Option + follow-up prompt pairing** (pre-answer the prompt an option
  deterministically opens: "if REMOVE chosen, remove Strike"; stash + apply,
  re-ask only on content mismatch).
- **D. Reward-set bundling** (one call per reward set answering card pick +
  potion + relic at once).
- **E. Standing directives** (act-level strategy statement executed by a local
  heuristic, escalate on novelty) — flagged as optional / probably defer.

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

## Open questions (not yet discussed)

- Async/pending machinery shape (decision parking, cancellation on boundary) —
  the tri-state answer and vote gate already anticipate it; mechanics TBD.
- Brain lifecycle: when brains are constructed (lobby add / resume), persona
  config surface (name, model, key source, prompt).
- Failure policy for a brain that errors or times out mid-decision (fall back
  to trivial? park forever + watchdog? per-usecase?).
- Token/cost telemetry (log per-call token counts into the event log?).
