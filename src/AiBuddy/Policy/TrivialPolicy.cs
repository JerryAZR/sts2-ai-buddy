using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Policy;

/// <summary>
/// v1 trivial policy: pure decision functions over game state. Shared by phantom
/// drivers and the debug local-player autopilot. This is the seam where a real
/// brain (heuristic or LLM) plugs in later — decisions only, never actuation.
/// Drivers re-validate every returned choice before actuating it (the brain is
/// untrusted, per design §3.5).
/// </summary>
public static class TrivialPolicy
{
    /// <summary>
    /// Relics whose obtain-flow opens a reward-set screen we can't fabricate yet (M2 subset carve-out).
    /// Empirically verified by NeowRelicTestDriver (the static audit missed NeowsBones/SmallCapsule):
    /// all four hang awaiting the phantom's reward selection. M3 RewardsSet fabrication unblocks all four.
    /// </summary>
    internal static readonly HashSet<string> DeferredRewardRelics = new() { "LOST_COFFER", "KALEIDOSCOPE", "NEOWS_BONES", "SMALL_CAPSULE" };

    /// <summary>
    /// First valid option. Valid = not locked, won't kill the player, not deferred.
    /// Returns null when no valid option exists (caller logs + waits for a better policy).
    /// </summary>
    public static int? PickEventOption(EventModel ev)
    {
        IReadOnlyList<EventOption> options = ev.CurrentOptions;
        for (int i = 0; i < options.Count; i++)
        {
            EventOption o = options[i];
            if (o.IsLocked || o.WillKillPlayer?.Invoke(ev.Owner) == true)
            {
                continue;
            }
            if (o.Relic != null && DeferredRewardRelics.Contains(o.Relic.Id.Entry))
            {
                continue;
            }
            return i;
        }
        Log.Warn($"[AiBuddy] No valid option in event {ev.Id} for player {ev.Owner.NetId}.");
        return null;
    }

    /// <summary>Index-choice prompts (choose-a-card, bundles): always take the first. Never skips in v1.</summary>
    public static int PickIndex(int optionCount) => 0;

    /// <summary>
    /// Brain implementation (see BuddyBrain.cs): greedily plans a full-turn batch —
    /// every playable card in hand order, with a locally simulated energy budget
    /// (no engine calls between picks). Rejected cards are never re-planned; cards
    /// with target types we can't parameterize (TargetedNoCreature/Osty) are skipped
    /// per the refusal contract. Drawn cards mid-batch aren't planned (they didn't
    /// exist at decision time) — the brain is asked again after the batch settles.
    /// </summary>
    public static BuddyIntent PlanTurnIntent(BuddyTurnView view)
    {
        PlayerCombatState? combat = view.Player.PlayerCombatState;
        if (combat == null)
        {
            return EndTurnIntent.Instance;
        }
        int energy = combat.Energy;
        var plays = new List<PlannedPlay>();
        foreach (CardModel card in combat.Hand.Cards)
        {
            if (view.RejectedThisTurn.Contains(card))
            {
                continue; // the engine refused this card this turn — never re-propose
            }
            // Intrinsic playability (keywords, hooks, card logic); energy is simulated below.
            if (!card.CanPlay(out UnplayableReason reason, out _)
                && reason != UnplayableReason.EnergyCostTooHigh)
            {
                continue;
            }
            int cost = card.EnergyCost.CostsX ? Math.Max(energy, 0) : card.EnergyCost.GetResolved();
            if (cost > energy || (card.EnergyCost.CostsX && energy == 0))
            {
                continue;
            }
            if (!TryFindTarget(card, view.Player, out Creature? target))
            {
                continue; // needs a target we can't supply — skip (refusal contract)
            }
            energy -= cost;
            plays.Add(new PlannedPlay(card, target));
        }
        return plays.Count > 0 ? new PlayBatch(plays) : EndTurnIntent.Instance;
    }

    /// <summary>
    /// Target resolution for the planner. Enemy/ally targets are creatures, exactly
    /// as a client's UI would supply them. NEVER plan a target-requiring card without
    /// a valid target — a null-target play is cancelled at execution, and before the
    /// plan-executor redesign that class flooded the action queue (Coordinate bug).
    /// </summary>
    private static bool TryFindTarget(CardModel card, Player player, out Creature? target)
    {
        switch (card.TargetType)
        {
            case TargetType.AnyEnemy:
                target = player.Creature.CombatState?.HittableEnemies.FirstOrDefault(e => card.CanPlayTargeting(e));
                return target != null;
            case TargetType.AnyAlly:
            case TargetType.AnyPlayer:
                target = player.RunState.Players
                    .Where(p => p != player && !p.Creature.IsDead)
                    .Select(p => p.Creature)
                    .FirstOrDefault(c => card.CanPlayTargeting(c));
                return target != null;
            case TargetType.TargetedNoCreature:
            case TargetType.Osty:
                target = null;
                return false; // unsupported targeting in v1
            default:
                target = null; // None/Self/AllEnemies/RandomEnemy/AllAllies: no target selection
                return true;
        }
    }

    /// <summary>
    /// Multi-select prompts: the first MinSelect options (or none when cancelable
    /// with no minimum). Returns null when the constraints are unsatisfiable —
    /// fabricating a short selection would send a result the game considers invalid,
    /// so the driver must skip fabrication and log instead.
    /// </summary>
    public static List<int>? PickIndexes(int optionCount, int minSelect, int maxSelect, bool cancelable)
    {
        if (optionCount < minSelect || maxSelect < minSelect)
        {
            return null; // unsatisfiable — refuse rather than emit an invalid selection
        }
        if (cancelable && minSelect == 0)
        {
            return new List<int>();
        }
        int count = Math.Clamp(Math.Max(minSelect, 1), 0, Math.Min(maxSelect, optionCount));
        return Enumerable.Range(0, count).ToList();
    }

    /// <summary>
    /// Map votes. Locked design decision: phantoms mirror the host player's vote
    /// (abstention risks stalling the lobby). Local autopilot takes the first child
    /// of the current coord (first row-0 point at run start).
    /// </summary>
    public static MapCoord? PickMapDestination(RunState state, Player player, MapCoord? hostVote)
    {
        if (!LocalContext.IsMe(player))
        {
            return hostVote;
        }
        if (state.CurrentMapCoord is MapCoord current)
        {
            return state.Map.GetPoint(current)?.Children.FirstOrDefault()?.coord;
        }
        return state.Map.GetPointsInRow(0).FirstOrDefault()?.coord;
    }
}
