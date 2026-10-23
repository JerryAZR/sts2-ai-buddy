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
