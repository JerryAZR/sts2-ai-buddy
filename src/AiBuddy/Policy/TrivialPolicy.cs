using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Map;

namespace AiBuddy.Policy;

/// <summary>
/// v1 trivial brain: PURE functions over detached views (see BuddyBrain.cs) — no
/// engine references in or out, so every method is unit-testable without the game.
/// This is the seam where a real brain (heuristic or LLM) plugs in later.
/// The control layer re-validates every returned choice before actuating it
/// (the brain is untrusted, per design §3.5).
/// </summary>
public static class TrivialPolicy
{
    /// <summary>
    /// Relics whose obtain-flow opens a reward-set screen we can't fabricate yet (M2 subset carve-out).
    /// Empirically verified by NeowRelicTestDriver; M3 RewardsSet fabrication unblocks all four.
    /// </summary>
    internal static readonly HashSet<string> DeferredRewardRelics = new(); // empty since M3 rewards fabrication landed

    /// <summary>
    /// Host-side decision randomness. Never touches the game's RNG streams — the
    /// fabricated RESULT enters the sim identically on every peer, the dice roll
    /// itself is not sim state.
    /// </summary>
    private static readonly Random Rng = new();

    /// <summary>
    /// Shop purchases (user-locked trivial rule): candidates = 3rd relic, colorless
    /// cards, potions (skipped when no slot open), card removal; shuffled; buy all
    /// affordable in that order. Returns indexes into the candidate list.
    /// </summary>
    public static IReadOnlyList<int> PickShopPurchases(IReadOnlyList<ShopCandidate> candidates, int gold, bool potionSlotOpen)
    {
        var order = candidates.Select((c, i) => (c, i))
            .Where(x => x.c.Kind != "potion" || potionSlotOpen)
            .ToList();
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = Rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        var buys = new List<int>();
        int budget = gold;
        foreach ((ShopCandidate c, int i) in order)
        {
            if (c.Cost <= budget)
            {
                buys.Add(i);
                budget -= c.Cost;
            }
        }
        return buys;
    }

    /// <summary>Rest site (user-locked trivial rule): HP below half → heal; otherwise a random other option. Ally-targeting options (Mend) are excluded — their local flow needs a target UI the trivial brain can't answer (brain-worthy later).</summary>
    public static int? PickRestSiteOption(int currentHp, int maxHp, IReadOnlyList<RestSiteOptionView> options)
    {
        RestSiteOptionView? heal = options.FirstOrDefault(o => o.OptionId == "HEAL");
        if (currentHp * 2 < maxHp && heal != null)
        {
            return heal.Index;
        }
        var others = options.Where(o => o.OptionId != "HEAL" && !o.NeedsAllyTarget).ToList();
        if (others.Count > 0)
        {
            return others[Rng.Next(others.Count)].Index;
        }
        return heal?.Index; // only heal left — take it
    }

    /// <summary>Card reward (user-locked trivial rule): deck below 20 → take a random card; at 20+ → 25% skip, else random.</summary>
    public static int? PickCardReward(int optionCount, int deckCount)
    {
        if (optionCount <= 0)
        {
            return null;
        }
        if (deckCount < 20)
        {
            return Rng.Next(optionCount);
        }
        return Rng.Next(4) == 0 ? null : Rng.Next(optionCount);
    }

    /// <summary>Card removal (user-locked trivial rule): curses/statuses first, then Strikes, then Defends, else don't remove (null).</summary>
    public static int? PickCardRemoval(IReadOnlyList<RemovalOption> options)
    {
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].IsCurseOrStatus)
            {
                return i;
            }
        }
        int strike = -1, defend = -1;
        for (int i = 0; i < options.Count; i++)
        {
            if (strike < 0 && options[i].CardId.StartsWith("STRIKE"))
            {
                strike = i;
            }
            if (defend < 0 && options[i].CardId.StartsWith("DEFEND"))
            {
                defend = i;
            }
        }
        return strike >= 0 ? strike : defend >= 0 ? defend : null;
    }

    /// <summary>
    /// Combat turn: greedy full-hand batch with a locally simulated energy budget.
    /// Legality was precomputed by the control layer (IntrinsicallyPlayable,
    /// LegalTargetIds) — the brain only sequences and spends. Rejected cards are
    /// never re-proposed. Cards drawn mid-batch aren't in this view; the brain is
    /// asked again after the batch settles.
    /// </summary>
    public static BuddyIntent PlanTurnIntent(CombatView view)
    {
        int energy = view.Energy;
        var plays = new List<PlannedPlay>();
        foreach (CombatCardView card in view.Hand)
        {
            if (view.RejectedCardIds.Contains(card.CardId) || !card.IntrinsicallyPlayable)
            {
                continue;
            }
            int cost = card.CostsX ? energy : card.Cost;
            if (cost > energy || (card.CostsX && energy == 0))
            {
                continue;
            }
            uint? target = card.LegalTargetIds.Count > 0 ? card.LegalTargetIds[0] : null;
            if (card.TargetKind is TargetType.AnyEnemy or TargetType.AnyAlly or TargetType.AnyPlayer && target == null)
            {
                continue; // needs a target, none legal — skip
            }
            energy -= cost;
            plays.Add(new PlannedPlay(card.HandIndex, card.CardId, target));
        }
        return plays.Count > 0 ? new PlayBatch(plays) : EndTurnIntent.Instance;
    }

    /// <summary>
    /// First valid event option. Valid = not locked, won't kill the player, not a
    /// deferred reward-set relic. Returns null when no valid option exists.
    /// </summary>
    public static int? PickEventOption(EventView view)
    {
        foreach (EventOptionView option in view.Options)
        {
            if (option.Locked || option.WillKill)
            {
                continue;
            }
            if (option.RelicId != null && DeferredRewardRelics.Contains(option.RelicId))
            {
                continue;
            }
            return option.Index;
        }
        return null;
    }

    /// <summary>Map votes: phantoms mirror the host (locked decision); the local autopilot takes the first legal destination.</summary>
    public static MapCoord? PickMapDestination(MapPickView view)
        => view.MirrorHostVote
            ? view.HostVote
            : view.LegalDestinations.Count > 0 ? view.LegalDestinations[0] : null; // struct: FirstOrDefault would return default(MapCoord), not null

    /// <summary>Index-choice prompts (choose-a-card, bundles): always take the first. Never skips in v1.</summary>
    public static int PickIndex(int optionCount) => 0;

    /// <summary>Treasure relic vote: random among relics nobody else is voting for —
    /// duplicate votes trigger an RPS relic fight (auto-resolved model-side, but it
    /// breaks the award animation's holder lookup on every peer). Humans dedup by
    /// watching live votes; we dedup from the synchronizer plus our in-flight picks.
    /// Defensive fallback when all are taken (relic count < player count): any index.</summary>
    public static int PickTreasureRelic(int relicCount, IReadOnlySet<int> takenIndices)
    {
        var free = Enumerable.Range(0, relicCount).Where(i => !takenIndices.Contains(i)).ToList();
        return free.Count > 0 ? free[Rng.Next(free.Count)] : Rng.Next(relicCount);
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
}
