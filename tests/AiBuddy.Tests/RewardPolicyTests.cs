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
