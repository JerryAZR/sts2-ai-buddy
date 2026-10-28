using AiBuddy.Drivers;
using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using Xunit;

namespace AiBuddy.Tests;

/// <summary>
/// Crystal Sphere budget fabrication (user-locked policy): budget = 3 (Uncover Future)
/// or 6 (Payment Plan) points; cards and rare potion cost 1, relic costs 4; each item
/// at most once; plus random gold (20/30/40) and 0-2 common potions (2 is rare).
/// The curse is NEVER fabricated (guest-side asymmetry: its ToReward is null and the
/// Doubt add is a host-stamped SyncLocal send).
/// </summary>
public class CrystalSpherePolicyTests
{
    [Fact]
    public void PickCrystalSphereRewards_AlwaysWithinBudget_AndDistinct()
    {
        foreach (int budget in new[] { 3, 6 })
        {
            for (int i = 0; i < 300; i++)
            {
                var picks = TrivialPolicy.PickCrystalSphereRewards(budget);
                Assert.Equal(picks.Count, picks.Distinct().Count());
                Assert.True(picks.Sum(CrystalSphereCatalog.Cost) <= budget,
                    $"budget {budget}: picks {string.Join(",", picks)} cost {picks.Sum(CrystalSphereCatalog.Cost)}");
            }
        }
    }

    [Fact]
    public void PickCrystalSphereRewards_Budget3_NeverAffordsRelic()
    {
        for (int i = 0; i < 300; i++)
        {
            Assert.DoesNotContain(CrystalSphereItemId.Relic, TrivialPolicy.PickCrystalSphereRewards(3));
        }
    }

    [Fact]
    public void PickCrystalSphereRewards_Budget6_SometimesTakesRelic()
    {
        // The relic must be reachable (shuffle + greedy), not silently never picked.
        bool sawRelic = Enumerable.Range(0, 300)
            .Any(_ => TrivialPolicy.PickCrystalSphereRewards(6).Contains(CrystalSphereItemId.Relic));
        Assert.True(sawRelic, "relic was never picked with budget 6 across 300 rolls");
    }

    [Fact]
    public void ValidatePicks_RejectsDupesAndOverbudget_AcceptsEmptyAndValid()
    {
        Assert.True(CrystalSphereDriver.ValidatePicks(3, new[] { CrystalSphereItemId.RareCard, CrystalSphereItemId.RarePotion }));
        Assert.True(CrystalSphereDriver.ValidatePicks(3, System.Array.Empty<CrystalSphereItemId>())); // abstain = zero rewards, legitimate
        Assert.False(CrystalSphereDriver.ValidatePicks(3, new[] { CrystalSphereItemId.Relic })); // 4 > 3
        Assert.False(CrystalSphereDriver.ValidatePicks(6, new[] { CrystalSphereItemId.Relic, CrystalSphereItemId.Relic })); // dupe
        Assert.False(CrystalSphereDriver.ValidatePicks(6, new[] { CrystalSphereItemId.Relic, CrystalSphereItemId.RareCard, CrystalSphereItemId.UncommonCard, CrystalSphereItemId.CommonCard })); // 7 > 6
    }

    [Fact]
    public void BuildPayload_CanonicalOrderAndFlags()
    {
        var payload = CrystalSphereDriver.BuildPayload(
            new[] { CrystalSphereItemId.CommonCard, CrystalSphereItemId.Relic }, // deliberately unsorted input
            goldAmount: 40, commonPotionCount: 2);

        Assert.Equal(new[]
        {
            CrystalSphereItemType.Relic,
            CrystalSphereItemType.CardReward, // common card
            CrystalSphereItemType.Potion, CrystalSphereItemType.Potion, // 2 common potions
            CrystalSphereItemType.Gold, CrystalSphereItemType.Gold, // 40 = big + small
        }, payload.Select(p => p.type));
        Assert.Equal(MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Common, payload[1].cardRarity);
        Assert.Equal(MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Common, payload[2].potionRarity);
        Assert.True(payload[4].isBigGold);
        Assert.False(payload[5].isBigGold);
    }

    [Fact]
    public void BuildPayload_GoldMapping()
    {
        Assert.Equal(2, CrystalSphereDriver.BuildPayload(System.Array.Empty<CrystalSphereItemId>(), 20, 0).Count); // small+small
        var thirty = CrystalSphereDriver.BuildPayload(System.Array.Empty<CrystalSphereItemId>(), 30, 0);
        Assert.Single(thirty);
        Assert.True(thirty[0].isBigGold);
    }

    [Fact]
    public void Rolls_GoldUniform_PotionsTwoIsRare()
    {
        var rng = new Random(42);
        var golds = Enumerable.Range(0, 900).Select(_ => CrystalSphereDriver.RollGold(rng)).ToList();
        Assert.All(golds, g => Assert.Contains(g, new[] { 20, 30, 40 }));

        var potions = Enumerable.Range(0, 900).Select(_ => CrystalSphereDriver.RollCommonPotionCount(rng)).ToList();
        Assert.All(potions, p => Assert.InRange(p, 0, 2));
        int twos = potions.Count(p => p == 2);
        Assert.True(twos < potions.Count(p => p == 0) && twos < potions.Count(p => p == 1),
            $"2 potions should be rare; got 0:{potions.Count(p => p == 0)} 1:{potions.Count(p => p == 1)} 2:{twos}");
    }

    [Fact]
    public void BuildPayload_NeverContainsCurse()
    {
        // Every catalog id maps to a non-curse payload entry — pin the whole space.
        var everything = CrystalSphereDriver.BuildPayload(
            CrystalSphereCatalog.All, goldAmount: 40, commonPotionCount: 2);
        Assert.DoesNotContain(everything, p => p.type == CrystalSphereItemType.Curse);
    }
}
