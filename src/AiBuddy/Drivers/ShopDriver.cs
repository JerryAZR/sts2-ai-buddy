using AiBuddy.Phantom;
using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
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
        if (!LocalContext.IsMe(player))
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
            Observability.EventLog.Write("mod", LocalContext.IsMe(player) ? "autopilot_shop_buy" : "phantom_shop_buy",
                new { netId = player.NetId, entry = entry.GetType().Name, cost = entry.Cost, gold = player.Gold });
            TaskHelper.RunSafely(entry.OnTryPurchaseWrapper(inventory));
            bought = true;
            break; // one purchase per tick
        }
        if (!bought && LocalContext.IsMe(player) && PhantomSession.Current is { } session)
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
}
