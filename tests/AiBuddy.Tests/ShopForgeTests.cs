using AiBuddy.Drivers;
using AiBuddy.Patches;
using AiBuddy.Phantom;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using Xunit;

namespace AiBuddy.Tests;

/// <summary>
/// Shop purchase forging contract: while a phantom purchase is in flight
/// (PhantomPurchaseContext), the RewardSynchronizer.SyncLocal* sends are suppressed
/// and re-forged with the phantom as wire sender and a payload identical to what
/// vanilla builds (decompiled 64226-64324). Null context = vanilla, untouched.
///
/// Not covered here (needs a live game / peers): the actual wire broadcast
/// (PhantomNet.BroadcastAsPhantom no-ops without a host NetService), the Harmony
/// suppression itself, and the card-removal bypass's command sequence
/// (CardSelectCmd/PlayerCmd/CardPileCmd are static game commands).
/// </summary>
public class ShopForgeTests
{
    private static readonly RunLocation Location = new(actIndex: 1, coord: new MapCoord(2, 3), roomId: null);

    private static async Task<ulong?> ReadContextAfterAwaits(ulong netId)
    {
        // Mirrors ShopDriver.RunPhantomPurchase: set inside the async flow.
        PhantomPurchaseContext.Purchaser = netId;
        await Task.Yield();
        await Task.Delay(1);
        return PhantomPurchaseContext.Purchaser;
    }

    [Fact]
    public async Task PurchaseContext_SetInsideAsyncFlow_SurvivesAwaits_DoesNotLeakToCaller()
    {
        Assert.Null(PhantomPurchaseContext.Purchaser);

        ulong? inside = await ReadContextAfterAwaits(PhantomSession.DefaultBaseNetId);

        Assert.Equal(PhantomSession.DefaultBaseNetId, inside);
        Assert.Null(PhantomPurchaseContext.Purchaser); // caller's context untouched — host shop stays vanilla
    }

    [Fact]
    public async Task PurchaseContext_ConcurrentFlows_KeepTheirOwnPurchaser()
    {
        ulong? a = PhantomSession.DefaultBaseNetId;
        ulong? b = PhantomSession.DefaultBaseNetId + 1;
        ulong?[] results = await Task.WhenAll(ReadContextAfterAwaits(a.Value), ReadContextAfterAwaits(b.Value));
        Assert.Equal(a, results[0]);
        Assert.Equal(b, results[1]);
    }

    [Fact]
    public void Forge_NullContext_ReturnsNull_VanillaSendRuns()
    {
        Assert.Null(PhantomPurchaseContext.Purchaser);

        Assert.Null(ShopPurchaseForgePatch.ForgeGoldLost(75, Location));
        Assert.Null(ShopPurchaseForgePatch.ForgeObtainedCard(null!, Location));
        Assert.Null(ShopPurchaseForgePatch.ForgeObtainedRelic(null!, Location));
        Assert.Null(ShopPurchaseForgePatch.ForgeObtainedPotion(null!, Location));
    }

    [Fact]
    public async Task ForgeGoldLost_PhantomContext_PhantomSenderAndVanillaPayload()
    {
        var send = await InPurchaseContext(() => ShopPurchaseForgePatch.ForgeGoldLost(75, Location));

        Assert.NotNull(send);
        Assert.Equal(PhantomSession.DefaultBaseNetId, send.Value.Sender);
        GoldLostMessage message = Assert.IsType<GoldLostMessage>(send.Value.Message);
        Assert.Equal(75, message.goldLost);
        Assert.Equal(Location, message.location);
    }

    [Fact]
    public async Task ForgeObtained_PhantomContext_PhantomSenderAndVanillaPayload()
    {
        var card = await InPurchaseContext(() => ShopPurchaseForgePatch.ForgeObtainedCard(null!, Location));
        var relic = await InPurchaseContext(() => ShopPurchaseForgePatch.ForgeObtainedRelic(null!, Location));
        var potion = await InPurchaseContext(() => ShopPurchaseForgePatch.ForgeObtainedPotion(null!, Location));

        RewardObtainedMessage cardMessage = Assert.IsType<RewardObtainedMessage>(card!.Value.Message);
        Assert.Equal(PhantomSession.DefaultBaseNetId, card.Value.Sender);
        Assert.Equal(RewardType.Card, cardMessage.rewardType);
        Assert.False(cardMessage.wasSkipped); // only the Obtained (never Skipped) variants fire in a purchase
        Assert.Equal(Location, cardMessage.location);

        RewardObtainedMessage relicMessage = Assert.IsType<RewardObtainedMessage>(relic!.Value.Message);
        Assert.Equal(PhantomSession.DefaultBaseNetId, relic.Value.Sender);
        Assert.Equal(RewardType.Relic, relicMessage.rewardType);
        Assert.False(relicMessage.wasSkipped);

        RewardObtainedMessage potionMessage = Assert.IsType<RewardObtainedMessage>(potion!.Value.Message);
        Assert.Equal(PhantomSession.DefaultBaseNetId, potion.Value.Sender);
        Assert.Equal(RewardType.Potion, potionMessage.rewardType);
        Assert.False(potionMessage.wasSkipped);
    }

    /// <summary>Runs the forge inside a phantom-purchase async flow, like ShopDriver does.</summary>
    private static async Task<ShopPurchaseForgePatch.ForgedSend?> InPurchaseContext(Func<ShopPurchaseForgePatch.ForgedSend?> forge)
    {
        PhantomPurchaseContext.Purchaser = PhantomSession.DefaultBaseNetId;
        await Task.Yield();
        return forge();
    }

    [Fact]
    public void RemovalPrefs_MatchVanillaDoMerchantCardRemoval()
    {
        // Decompiled 63237-63240: RemoveSelectionPrompt, 1 card, Cancelable, RequireManualConfirmation=true.
        CardSelectorPrefs prefs = ShopDriver.RemovalPrefs;
        Assert.Equal(1, prefs.MinSelect);
        Assert.Equal(1, prefs.MaxSelect);
        Assert.True(prefs.Cancelable);
        Assert.True(prefs.RequireManualConfirmation);
    }

    /// <summary>EA drift tripwire for the Harmony targets (Verified in-game by ModEntry; pinned here without launching the game).</summary>
    [Theory]
    [InlineData("SyncLocalGoldLost")]
    [InlineData("SyncLocalObtainedCard")]
    [InlineData("SyncLocalObtainedRelic")]
    [InlineData("SyncLocalObtainedPotion")]
    public void RewardSynchronizer_SyncLocalTargets_Exist(string methodName)
    {
        Assert.NotNull(typeof(RewardSynchronizer).GetMethod(methodName));
    }
}
