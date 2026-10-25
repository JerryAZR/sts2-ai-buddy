using AiBuddy.Phantom;
using AiBuddy.Policy;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Drives shop purchases. Shops are per-player (MerchantRoom.Inventories by slot);
/// the purchase seam is MerchantEntry.OnTryPurchaseWrapper — the same call the UI
/// makes — for phantoms AND the local autopilot (no phantom-specific UI exists,
/// and the local UI path reports through the same inventory state).
/// Brain rule: 3rd relic / colorless cards / potions (slot permitting) / card
/// removal, shuffled, buy all affordable. One purchase per tick per player.
///
/// Phantom forging (M-shop): stocked entries run inside PhantomPurchaseContext so
/// ShopPurchaseForgePatch re-stamps their auto-fired sync sends to the phantom;
/// card removal bypasses OnTryPurchase entirely (vanilla's path is local-player-
/// semantic in both halves — it would hijack the host's UI and stamp the host).
/// The local autopilot (IsMe) stays 100% vanilla.
/// </summary>
public static class ShopDriver
{
    public static void Drive(Player player, RunState state)
    {
        if (state.CurrentRoom is not MerchantRoom shop)
        {
            return;
        }
        MerchantInventory? inventory = shop.Inventories.ElementAtOrDefault(state.GetPlayerSlotIndex(player));
        if (inventory == null)
        {
            return;
        }
        bool isPhantom = !LocalContext.IsMe(player);
        if (isPhantom)
        {
            PhantomSession.Current?.MarkFlowActive(player.NetId, PhantomSession.RoomFlow.Shop);
        }

        // Candidates per the user-locked rule: 3rd relic, colorless cards, potions, card removal.
        var entries = new List<MerchantEntry>();
        var candidates = new List<ShopCandidate>();
        void Add(IEnumerable<MerchantEntry> source, string kind)
        {
            foreach (MerchantEntry entry in source)
            {
                entries.Add(entry);
                candidates.Add(new ShopCandidate(kind, entry.Cost));
            }
        }
        if (inventory.RelicEntries.Count >= 3)
        {
            Add(new[] { inventory.RelicEntries[2] }, "relic3");
        }
        Add(inventory.ColorlessCardEntries, "colorless");
        Add(inventory.PotionEntries, "potion");
        if (inventory.CardRemovalEntry is { } removal)
        {
            Add(new[] { removal }, "removal");
        }

        IReadOnlyList<int> buys = TrivialPolicy.PickShopPurchases(candidates, player.Gold, player.HasOpenPotionSlots);
        bool bought = false;
        foreach (int index in buys)
        {
            MerchantEntry entry = entries[index];
            if (!entry.IsStocked || !entry.EnoughGold)
            {
                continue; // affordability/stock re-checked at actuation (the brain saw a snapshot)
            }
            if (candidates[index].Kind == "potion" && !player.HasOpenPotionSlots)
            {
                continue;
            }
            Observability.EventLog.Write("mod", isPhantom ? "phantom_shop_buy" : "autopilot_shop_buy",
                new { netId = player.NetId, entry = entry.GetType().Name, cost = entry.Cost, gold = player.Gold });
            if (!isPhantom)
            {
                TaskHelper.RunSafely(entry.OnTryPurchaseWrapper(inventory));
            }
            else if (entry is MerchantCardRemovalEntry removalEntry)
            {
                // No PhantomPurchaseContext here: the replicated removal chain sends
                // nothing itself in v0.111.0 (the forge below is the only send).
                TaskHelper.RunSafely(BuyPhantomCardRemoval(player, removalEntry));
            }
            else
            {
                TaskHelper.RunSafely(RunPhantomPurchase(player.NetId, entry, inventory));
            }
            bought = true;
            break; // one purchase per tick
        }
        if (!bought && !isPhantom && PhantomSession.Current is { } session)
        {
            // Done shopping: room exit is a local-UI step (NMerchantRoom.ProceedButton).
            session.LocalShopPendingProceed = true;
        }
        else if (!bought)
        {
            // Brain bought everything it wanted: terminal answer for this room's shop.
            PhantomSession.Current?.MarkFlowBrainDone(player.NetId, PhantomSession.RoomFlow.Shop);
        }
    }

    /// <summary>
    /// Phantom purchase of a stocked entry. The entry's OnTryPurchase fires its sync
    /// sends (GoldLostMessage / RewardObtainedMessage) AFTER awaits, so a plain static
    /// would race; the AsyncLocal set here flows with this async flow across those
    /// awaits, and ShopPurchaseForgePatch suppresses + re-forges each send with the
    /// phantom as wire sender. The caller's context is untouched (async-flow locals
    /// never leak back), so the host's own shop stays vanilla.
    /// </summary>
    private static async Task RunPhantomPurchase(ulong netId, MerchantEntry entry, MerchantInventory inventory)
    {
        PhantomPurchaseContext.Purchaser = netId;
        await entry.OnTryPurchaseWrapper(inventory);
    }

    /// <summary>The exact prefs vanilla's DoMerchantCardRemoval builds (decompiled 63237-63240).</summary>
    internal static CardSelectorPrefs RemovalPrefs => new(CardSelectorPrefs.RemoveSelectionPrompt, 1)
    {
        Cancelable = true,
        RequireManualConfirmation = true,
    };

    /// <summary>
    /// Phantom card removal bypasses MerchantCardRemovalEntry.OnTryPurchase entirely:
    /// vanilla's path (OneOffSynchronizer.DoLocalMerchantCardRemoval) opens the HOST's
    /// deck-removal UI, charges the host's gold, and stamps the host. Instead replicate
    /// DoMerchantCardRemoval (decompiled 63235-63252) against the phantom and forge the
    /// MerchantCardRemovalMessage guests' sims await (its handler keys on the wire
    /// sender). The card select itself rides the normal ChoiceDriver capture/fabricate
    /// path, which already broadcasts the forged PlayerChoiceMessage guests wait on.
    /// </summary>
    private static async Task BuyPhantomCardRemoval(Player player, MerchantCardRemovalEntry entry)
    {
        int goldCost = entry.Cost;
        // Vanilla ordering (DoLocalMerchantCardRemoval): the message goes out before
        // the removal flow runs, even if the choice is later cancelled.
        PhantomNet.BroadcastAsPhantom(player.NetId, new MerchantCardRemovalMessage
        {
            goldCost = goldCost,
            Location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation,
        });
        CardModel? card = (await CardSelectCmd.FromDeckForRemoval(player, RemovalPrefs)).FirstOrDefault();
        if (card == null)
        {
            return; // cancelled: no charge, slot stays stocked — same as vanilla
        }
        await PlayerCmd.LoseGold(goldCost, player, GoldLossType.Spent);
        await CardPileCmd.RemoveFromDeck(card);
        player.ExtraFields.CardShopRemovalsUsed++;
        entry.SetUsed(); // model flag, not the local UI node — keeps IsStocked false on later ticks
    }
}
