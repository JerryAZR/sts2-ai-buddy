using AiBuddy.Phantom;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Patches;

/// <summary>
/// Shop purchase forging (bucket 1): MerchantCardEntry/RelicEntry/PotionEntry
/// .OnTryPurchase fire GoldLostMessage + RewardObtainedMessage AFTER awaits, via
/// RewardSynchronizer.SyncLocal* — which sends through NetHostGameService and thus
/// stamps the HOST id. Guest handlers resolve the player from the wire sender id,
/// so a phantom purchase would debit/credit the host on every guest.
///
/// While PhantomPurchaseContext is set (a phantom purchase is in flight on this
/// async flow), suppress the host-stamped send and re-broadcast the IDENTICAL
/// message with the phantom as wire sender (byte-identical payload + forged
/// overrideSenderId = identical guest sim). Null context (host human shopping,
/// every non-shop SyncLocal* caller) runs vanilla untouched.
/// </summary>
[HarmonyPatch]
internal static class ShopPurchaseForgePatch
{
    /// <summary>A suppressed vanilla send to re-broadcast with Sender as wire id.</summary>
    internal readonly record struct ForgedSend(ulong Sender, INetMessage Message);

    // Same buffer RewardSynchronizer is constructed with (RunManager 39895).
    private static RunLocation CurrentLocation => RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(RewardSynchronizer), nameof(RewardSynchronizer.SyncLocalGoldLost))]
    private static bool SyncLocalGoldLost(int goldLost)
        => Apply(ForgeGoldLost(goldLost, CurrentLocation));

    [HarmonyPrefix]
    [HarmonyPatch(typeof(RewardSynchronizer), nameof(RewardSynchronizer.SyncLocalObtainedCard))]
    private static bool SyncLocalObtainedCard(CardModel card)
        => Apply(ForgeObtainedCard(card, CurrentLocation));

    [HarmonyPrefix]
    [HarmonyPatch(typeof(RewardSynchronizer), nameof(RewardSynchronizer.SyncLocalObtainedRelic))]
    private static bool SyncLocalObtainedRelic(RelicModel relic)
        => Apply(ForgeObtainedRelic(relic, CurrentLocation));

    [HarmonyPrefix]
    [HarmonyPatch(typeof(RewardSynchronizer), nameof(RewardSynchronizer.SyncLocalObtainedPotion))]
    private static bool SyncLocalObtainedPotion(PotionModel potion)
        => Apply(ForgeObtainedPotion(potion, CurrentLocation));

    // Message shapes mirror the vanilla SyncLocal* bodies field-for-field
    // (decompiled 64226-64324). wasSkipped is always false: only the Obtained
    // variants fire inside a purchase.

    internal static ForgedSend? ForgeGoldLost(int goldLost, RunLocation location)
        => Wrap(new GoldLostMessage { goldLost = goldLost, location = location });

    internal static ForgedSend? ForgeObtainedCard(CardModel card, RunLocation location)
        => Wrap(new RewardObtainedMessage { rewardType = RewardType.Card, cardModel = card, wasSkipped = false, location = location });

    internal static ForgedSend? ForgeObtainedRelic(RelicModel relic, RunLocation location)
        => Wrap(new RewardObtainedMessage { rewardType = RewardType.Relic, relicModel = relic, wasSkipped = false, location = location });

    internal static ForgedSend? ForgeObtainedPotion(PotionModel potion, RunLocation location)
        => Wrap(new RewardObtainedMessage { rewardType = RewardType.Potion, potionModel = potion, wasSkipped = false, location = location });

    private static ForgedSend? Wrap(INetMessage message)
        => PhantomPurchaseContext.Purchaser is ulong phantomNetId ? new ForgedSend(phantomNetId, message) : null;

    private static bool Apply(ForgedSend? send)
    {
        if (send is not { } forged)
        {
            return true; // no phantom purchase in flight: vanilla send
        }
        Observability.EventLog.Write("mod", "phantom_shop_forge",
            new { netId = forged.Sender, type = forged.Message.GetType().Name });
        PhantomNet.BroadcastAsPhantom(forged.Sender, forged.Message);
        return false; // suppress the host-stamped send
    }
}
