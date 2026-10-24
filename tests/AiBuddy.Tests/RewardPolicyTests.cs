using AiBuddy.Policy;
using Xunit;
namespace AiBuddy.Tests;

/// <summary>Reward decisions (user-locked trivial rules).</summary>
public class RewardPolicyTests
{
    [Fact]
    public void CardReward_SmallDeck_AlwaysTakesInRange()
    {
        for (int i = 0; i < 200; i++)
        {
            Assert.InRange(TrivialPolicy.PickCardReward(optionCount: 3, deckCount: 10)!.Value, 0, 2);
        }
    }

    [Fact]
    public void CardReward_BigDeck_SkipsAboutAQuarter()
    {
        int skips = Enumerable.Range(0, 2000).Count(_ => TrivialPolicy.PickCardReward(3, 25) == null);
        Assert.InRange(skips, 350, 650); // 25% ± generous slack
    }

    [Fact]
    public void CardRemoval_CurseFirst()
    {
        var options = new[] { new RemovalOption("STRIKE_IRONCLAD", false), new RemovalOption("ASCENDERS_BANE", true), new RemovalOption("DEFEND_IRONCLAD", false) };
        Assert.Equal(1, TrivialPolicy.PickCardRemoval(options));
    }

    [Fact]
    public void CardRemoval_StrikeBeforeDefend()
    {
        var options = new[] { new RemovalOption("DEFEND_IRONCLAD", false), new RemovalOption("STRIKE_IRONCLAD", false) };
        Assert.Equal(1, TrivialPolicy.PickCardRemoval(options));
    }

    [Fact]
    public void CardRemoval_NoCandidate_DontRemove()
    {
        var options = new[] { new RemovalOption("BASH", false), new RemovalOption("INFLAME", false) };
        Assert.Null(TrivialPolicy.PickCardRemoval(options));
    }
}

public class RestSitePolicyTests
{
    private static RestSiteOptionView[] Options(params string[] ids)
        => ids.Select((id, i) => new RestSiteOptionView(i, id, id == "MEND")).ToArray();

    [Fact]
    public void RestSite_BelowHalfHp_Heals()
    {
        Assert.Equal(1, TrivialPolicy.PickRestSiteOption(30, 80, Options("SMITH", "HEAL", "DIG")));
    }

    [Fact]
    public void RestSite_Healthy_PicksRandomNonHeal()
    {
        for (int i = 0; i < 50; i++)
        {
            int pick = TrivialPolicy.PickRestSiteOption(70, 80, Options("HEAL", "SMITH", "DIG"))!.Value;
            Assert.Contains(pick, new[] { 1, 2 });
        }
    }

    [Fact]
    public void RestSite_OnlyHealLeft_Heals()
    {
        Assert.Equal(0, TrivialPolicy.PickRestSiteOption(70, 80, Options("HEAL")));
    }

    [Fact]
    public void RestSite_Healthy_OnlyAllyTargetingAndHealLeft_FallsBackToHeal()
    {
        // The rest-site completion pin: when every preferred option is excluded
        // (ally-targeting) or already failed, the brain falls back to Heal — which
        // the engine guarantees succeeds — so a phantom rest site always completes.
        Assert.Equal(1, TrivialPolicy.PickRestSiteOption(80, 80, Options("MEND", "HEAL")));
    }

    [Fact]
    public void RestSite_Healthy_OnlyAllyTargetingLeft_NoMove()
    {
        Assert.Null(TrivialPolicy.PickRestSiteOption(80, 80, Options("MEND")));
    }
}

public class RestSiteAllyTargetTests
{
    [Fact]
    public void RestSite_AllyTargetingOptions_AreExcluded()
    {
        var options = new[] { new RestSiteOptionView(0, "HEAL", false), new RestSiteOptionView(1, "MEND", true), new RestSiteOptionView(2, "SMITH", false) };
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(2, TrivialPolicy.PickRestSiteOption(70, 80, options)); // healthy: never mend
        }
    }
}

public class ShopPolicyTests
{
    private static ShopCandidate C(string kind, int cost) => new(kind, cost);

    [Fact]
    public void Shop_BuysAllAffordable_SkipsUnaffordable()
    {
        // Invariants under shuffling: never overspend, and the leftover budget can't
        // afford any unpurchased candidate (greedy maximality in the shuffled order).
        var candidates = new[] { C("relic3", 150), C("colorless", 50), C("colorless", 45), C("removal", 75) };
        for (int trial = 0; trial < 100; trial++)
        {
            var buys = TrivialPolicy.PickShopPurchases(candidates, gold: 200, potionSlotOpen: true);
            int spent = buys.Sum(i => candidates[i].Cost);
            Assert.True(spent <= 200);
            int leftover = 200 - spent;
            foreach (int i in Enumerable.Range(0, candidates.Length).Except(buys))
            {
                Assert.True(candidates[i].Cost > leftover, $"candidate {i} affordable but not bought");
            }
        }
    }

    [Fact]
    public void Shop_NoPotionSlot_SkipsPotions()
    {
        var candidates = new[] { C("potion", 50), C("colorless", 50) };
        var buys = TrivialPolicy.PickShopPurchases(candidates, gold: 999, potionSlotOpen: false);
        Assert.Equal(new[] { 1 }, buys.ToArray());
    }

    [Fact]
    public void Shop_Broke_BuysNothing()
    {
        var candidates = new[] { C("relic3", 150), C("colorless", 50) };
        Assert.Empty(TrivialPolicy.PickShopPurchases(candidates, gold: 10, potionSlotOpen: true));
    }
}
