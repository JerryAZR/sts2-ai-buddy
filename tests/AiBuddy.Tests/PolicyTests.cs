using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Map;
using Xunit;

namespace AiBuddy.Tests;

/// <summary>
/// The trivial brain is pure (detached views in, id-based intents out), so these
/// tests pin the DECISION CONTRACT without the game: energy budgeting, refusal
/// semantics, rejection exclusion, target handling.
/// </summary>
public class PolicyTests
{
    // ===== PickIndexes (choice prompts) =====

    [Fact]
    public void PickIndexes_RequiredSingleChoice_TakesFirst()
    {
        Assert.Equal(new List<int> { 0 }, TrivialPolicy.PickIndexes(optionCount: 5, minSelect: 1, maxSelect: 1, cancelable: false));
    }

    [Fact]
    public void PickIndexes_CancelableWithNoMinimum_TakesNothing()
    {
        Assert.Empty(TrivialPolicy.PickIndexes(optionCount: 3, minSelect: 0, maxSelect: 1, cancelable: true));
    }

    [Fact]
    public void PickIndexes_MultiSelect_RespectsMinimum()
    {
        Assert.Equal(new List<int> { 0, 1 }, TrivialPolicy.PickIndexes(optionCount: 4, minSelect: 2, maxSelect: 2, cancelable: false));
    }

    /// <summary>
    /// Unsatisfiable constraints (fewer options than the minimum, or max below min)
    /// must be refused: fabricating a short selection sends a result the game
    /// considers invalid.
    /// </summary>
    [Fact]
    public void PickIndexes_UnsatisfiableConstraints_Refuse()
    {
        Assert.Null(TrivialPolicy.PickIndexes(optionCount: 0, minSelect: 1, maxSelect: 1, cancelable: false));
        Assert.Null(TrivialPolicy.PickIndexes(optionCount: 1, minSelect: 2, maxSelect: 3, cancelable: false));
        Assert.Null(TrivialPolicy.PickIndexes(optionCount: 5, minSelect: 3, maxSelect: 1, cancelable: false));
    }

    /// <summary>Contract matrix: result is null, or a valid selection (count in range, distinct in-range indexes). Never throws.</summary>
    [Fact]
    public void PickIndexes_NeverReturnsAnInvalidSelection()
    {
        for (int optionCount = 0; optionCount <= 6; optionCount++)
        for (int min = 0; min <= 3; min++)
        for (int max = 0; max <= 4; max++)
        foreach (bool cancelable in new[] { false, true })
        {
            List<int>? result = TrivialPolicy.PickIndexes(optionCount, min, max, cancelable);
            if (result == null)
            {
                Assert.True(optionCount < min || max < min,
                    $"refused a satisfiable prompt: options={optionCount} min={min} max={max} cancelable={cancelable}");
                continue;
            }
            Assert.InRange(result.Count, min, Math.Min(max, optionCount));
            Assert.Equal(result.Count, result.Distinct().Count());
            Assert.All(result, i => Assert.InRange(i, 0, optionCount - 1));
        }
    }

    [Fact]
    public void PickIndex_AlwaysTakesFirst()
    {
        Assert.Equal(0, TrivialPolicy.PickIndex(1));
        Assert.Equal(0, TrivialPolicy.PickIndex(42));
    }

    // ===== PlanTurnIntent (combat) =====

    private static CombatCardView Card(int index, string id, int cost,
        bool playable = true, TargetType targetKind = TargetType.None, params uint?[] legalTargets)
        => new(index, id, cost, CostsX: false, playable, targetKind, legalTargets);

    private static CombatView Combat(int energy, IReadOnlyList<CombatCardView> hand, IReadOnlySet<string>? rejected = null)
        => new(energy, 1001, hand, new List<CreatureView>(), rejected ?? new HashSet<string>(), new List<PlayOutcome>());

    [Fact]
    public void PlanTurn_EmptyHand_EndsTurn()
    {
        Assert.IsType<EndTurnIntent>(TrivialPolicy.PlanTurnIntent(Combat(3, new List<CombatCardView>())));
    }

    [Fact]
    public void PlanTurn_PlaysWhatEnergyAffords_InHandOrder()
    {
        var hand = new[] { Card(0, "A", 1), Card(1, "B", 1), Card(2, "C", 1) };
        var batch = Assert.IsType<PlayBatch>(TrivialPolicy.PlanTurnIntent(Combat(2, hand)));
        Assert.Equal(new[] { 0, 1 }, batch.Plays.Select(p => p.HandIndex).ToArray()); // 2 energy, three 1-cost cards
    }

    [Fact]
    public void PlanTurn_SkipsIntrinsicallyUnplayable()
    {
        var hand = new[] { Card(0, "CURSE", 0, playable: false), Card(1, "A", 1) };
        var batch = Assert.IsType<PlayBatch>(TrivialPolicy.PlanTurnIntent(Combat(1, hand)));
        Assert.Equal(1, Assert.Single(batch.Plays).HandIndex);
    }

    [Fact]
    public void PlanTurn_NeverReproposesRejectedCards()
    {
        var hand = new[] { Card(0, "A", 1), Card(1, "B", 1) };
        var rejected = new HashSet<string> { "A" };
        var batch = Assert.IsType<PlayBatch>(TrivialPolicy.PlanTurnIntent(Combat(3, hand, rejected)));
        Assert.Equal("B", Assert.Single(batch.Plays).CardId);
    }

    [Fact]
    public void PlanTurn_TargetedCardWithoutLegalTargets_IsSkipped()
    {
        var hand = new[] { Card(0, "ATTACK", 1, targetKind: TargetType.AnyEnemy) }; // no legal targets
        Assert.IsType<EndTurnIntent>(TrivialPolicy.PlanTurnIntent(Combat(3, hand)));
    }

    [Fact]
    public void PlanTurn_TargetedCard_TakesFirstLegalTarget()
    {
        var hand = new[] { Card(0, "ATTACK", 1, targetKind: TargetType.AnyEnemy, legalTargets: new uint?[] { 7u, 9u }) };
        var batch = Assert.IsType<PlayBatch>(TrivialPolicy.PlanTurnIntent(Combat(3, hand)));
        Assert.Equal(7u, Assert.Single(batch.Plays).TargetCombatId);
    }

    [Fact]
    public void PlanTurn_UntargetedCard_HasNullTarget()
    {
        var hand = new[] { Card(0, "POWER", 1) };
        var batch = Assert.IsType<PlayBatch>(TrivialPolicy.PlanTurnIntent(Combat(3, hand)));
        Assert.Null(Assert.Single(batch.Plays).TargetCombatId);
    }

    // ===== PickEventOption =====

    private static EventOptionView Opt(int i, bool locked = false, bool willKill = false, string? relic = null)
        => new(i, locked, willKill, relic);

    [Fact]
    public void PickEventOption_TakesFirstValid()
    {
        Assert.Equal(0, TrivialPolicy.PickEventOption(new EventView("EV", new[] { Opt(0), Opt(1) })));
    }

    [Fact]
    public void PickEventOption_SkipsLockedAndLethal()
    {
        Assert.Equal(2, TrivialPolicy.PickEventOption(new EventView("EV", new[] { Opt(0, locked: true), Opt(1, willKill: true), Opt(2) })));
    }

    [Fact]
    public void PickEventOption_SkipsDeferredRewardRelics()
    {
        Assert.Equal(1, TrivialPolicy.PickEventOption(new EventView("EV", new[] { Opt(0, relic: "LOST_COFFER"), Opt(1) })));
    }

    [Fact]
    public void PickEventOption_AllInvalid_ReturnsNull()
    {
        Assert.Null(TrivialPolicy.PickEventOption(new EventView("EV", new[] { Opt(0, locked: true) })));
    }

    // ===== PickMapDestination =====

    [Fact]
    public void PickMapDestination_Phantom_MirrorsHostVote()
    {
        var hostVote = new MapCoord(1, 2);
        var legal = new[] { new MapCoord(0, 1), new MapCoord(1, 2) };
        Assert.Equal(hostVote, TrivialPolicy.PickMapDestination(new MapPickView(MirrorHostVote: true, hostVote, legal)));
    }

    [Fact]
    public void PickMapDestination_Local_FirstLegal()
    {
        var legal = new[] { new MapCoord(0, 1), new MapCoord(1, 2) };
        Assert.Equal(legal[0], TrivialPolicy.PickMapDestination(new MapPickView(MirrorHostVote: false, null, legal)));
    }

    [Fact]
    public void PickMapDestination_NoLegalDestinations_Null()
    {
        Assert.Null(TrivialPolicy.PickMapDestination(new MapPickView(MirrorHostVote: false, null, new List<MapCoord>())));
    }
}
