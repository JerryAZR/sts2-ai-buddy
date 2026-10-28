using System.Reflection;
using AiBuddy.Phantom;
using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Crystal Sphere fabrication. The minigame is fully local per player; the sole wire
/// artifact is one CrystalSphereRewardsMessage (revealed items = type + rarity flag,
/// no positions/clicks/timing — recon-verified v0.111.0). The phantom's event clone
/// finishes on all peers without any minigame (non-local owners skip PlayMinigame);
/// what guests additionally await is this rewards message, whose handler only checks
/// sender != local and sender-in-CrystalSphere. We fabricate a plausible outcome per
/// the user-locked budget policy: budget = the option's divination count (3/6, read
/// from the event's DynamicVars), spent on catalog items (cards/rare potion 1pt,
/// relic 4pt), plus random gold (20/30/40) and 0-2 common potions (2 is rare).
/// Curse is NEVER in the payload: its ToReward is null on guests and its Doubt add
/// is a host-stamped SyncLocal send.
///
/// Determinism: the local item list and the forged payload are the SAME list in the
/// SAME order — each ToReward consumes sequential event-Rng draws on every peer.
/// No checksum risk: CrystalSphere.IsDeterministic is false (no exit checksum).
/// </summary>
public static class CrystalSphereDriver
{
    private static readonly Random Rng = new();

    private static readonly MethodInfo OfferRewards =
        typeof(OneOffSynchronizer).GetMethod("OfferCrystalSphereRewards", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException("OneOffSynchronizer.OfferCrystalSphereRewards");

    /// <summary>Point budget for a Crystal Sphere option, or null for non-minigame options. Reads the event's DynamicVars (drift-proof vs hardcoding 3/6).</summary>
    public static int? BudgetForOption(EventModel ev, string? optionTextKey)
    {
        if (ev.Id.Entry != "CRYSTAL_SPHERE" || optionTextKey == null)
        {
            return null;
        }
        if (optionTextKey.EndsWith("UNCOVER_FUTURE"))
        {
            return (int)ev.DynamicVars["UncoverFutureProphesizeCount"].BaseValue;
        }
        if (optionTextKey.EndsWith("PAYMENT_PLAN"))
        {
            return (int)ev.DynamicVars["PaymentPlanCount"].BaseValue;
        }
        return null;
    }

    /// <summary>Brain-contract validation: distinct ids, total cost within budget. Empty is legitimate (abstain = revealed nothing).</summary>
    public static bool ValidatePicks(int budget, IReadOnlyList<CrystalSphereItemId> picks)
        => picks.Distinct().Count() == picks.Count && picks.Sum(CrystalSphereCatalog.Cost) <= budget;

    /// <summary>Uniform 20/30/40.</summary>
    public static int RollGold(Random rng) => 20 + 10 * rng.Next(3);

    /// <summary>0-2 common potions; 2 is rare (45/45/10, user-locked spec).</summary>
    public static int RollCommonPotionCount(Random rng)
    {
        int roll = rng.Next(100);
        return roll < 45 ? 0 : roll < 90 ? 1 : 2;
    }

    /// <summary>
    /// The fabricated payload: catalog picks in canonical order, then common potions,
    /// then gold (20 = two small, 30 = one big, 40 = big + small). Plain data only —
    /// local application rehydrates via CrystalSphereItem.FromSerializable so local
    /// and wire are the same items in the same order by construction.
    /// </summary>
    public static List<SerializableCrystalSphereItem> BuildPayload(
        IReadOnlyList<CrystalSphereItemId> picks, int goldAmount, int commonPotionCount)
    {
        var payload = new List<SerializableCrystalSphereItem>();
        foreach (CrystalSphereItemId id in new[] { CrystalSphereItemId.Relic, CrystalSphereItemId.RareCard,
                     CrystalSphereItemId.UncommonCard, CrystalSphereItemId.CommonCard, CrystalSphereItemId.RarePotion })
        {
            if (!picks.Contains(id))
            {
                continue;
            }
            payload.Add(id switch
            {
                CrystalSphereItemId.Relic => new SerializableCrystalSphereItem { type = CrystalSphereItemType.Relic },
                CrystalSphereItemId.RarePotion => new SerializableCrystalSphereItem { type = CrystalSphereItemType.Potion, potionRarity = PotionRarity.Rare },
                _ => new SerializableCrystalSphereItem
                {
                    type = CrystalSphereItemType.CardReward,
                    cardRarity = id == CrystalSphereItemId.RareCard ? CardRarity.Rare
                        : id == CrystalSphereItemId.UncommonCard ? CardRarity.Uncommon : CardRarity.Common,
                },
            });
        }
        for (int i = 0; i < commonPotionCount; i++)
        {
            payload.Add(new SerializableCrystalSphereItem { type = CrystalSphereItemType.Potion, potionRarity = PotionRarity.Common });
        }
        int gold = goldAmount;
        if (gold >= 30)
        {
            payload.Add(new SerializableCrystalSphereItem { type = CrystalSphereItemType.Gold, isBigGold = true });
            gold -= 30;
        }
        while (gold >= 10)
        {
            payload.Add(new SerializableCrystalSphereItem { type = CrystalSphereItemType.Gold, isBigGold = false });
            gold -= 10;
        }
        return payload;
    }

    /// <summary>
    /// Fabricate the phantom's minigame outcome: apply the rewards locally (reflected
    /// OfferCrystalSphereRewards — DoLocalCrystalSphereRewards throws for non-local
    /// owners) and forge the message guests' sims await. Call immediately after the
    /// phantom's option choice so guests process option → rewards in order while
    /// GetEventForPlayer(phantom) is still the CrystalSphere.
    /// </summary>
    public static void FabricateRewards(Player player, int budget)
    {
        var picks = TrivialPolicy.PickCrystalSphereRewards(budget);
        if (!ValidatePicks(budget, picks))
        {
            Log.Warn($"[AiBuddy] Brain contract violation: crystal sphere picks [{string.Join(",", picks)}] exceed budget {budget}; fabricating nothing.");
            Observability.EventLog.Write("mod", "brain_contract_violation", new { usecase = "crystal_sphere", budget, picks = picks.ToArray() });
            return;
        }
        if (RunManager.Instance.EventSynchronizer.GetEventForPlayer(player) is not CrystalSphere sphere)
        {
            Log.Warn($"[AiBuddy] Crystal sphere fabrication refused: {player.NetId} is not in a CrystalSphere event.");
            return;
        }
        int gold = RollGold(Rng);
        int commonPotions = RollCommonPotionCount(Rng);
        List<SerializableCrystalSphereItem> payload = BuildPayload(picks, gold, commonPotions);
        List<CrystalSphereItem> items = payload.Select(p => CrystalSphereItem.FromSerializable(p, player)).ToList();

        Observability.EventLog.Write("mod", "phantom_crystal_sphere_rewards",
            new { netId = player.NetId, budget, picks = picks.ToArray(), gold, commonPotions });
        TaskHelper.RunSafely((System.Threading.Tasks.Task)OfferRewards.Invoke(
            RunManager.Instance.OneOffSynchronizer, new object[] { player, items, sphere.Rng })!);
        PhantomNet.BroadcastAsPhantom(player.NetId, new CrystalSphereRewardsMessage
        {
            rewards = payload,
            Location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation,
        });
    }
}
