namespace AiBuddy.Phantom;

/// <summary>
/// AsyncLocal "current phantom purchaser" for shop buys. Merchant entry purchases
/// fire their sync sends (GoldLostMessage / RewardObtainedMessage) AFTER awaits
/// (CardPileCmd.Add, PlayerCmd.LoseGold), stamped with the host id by vanilla
/// (RewardSynchronizer.SyncLocal*). A plain static would race; AsyncLocal flows
/// with the purchase's async flow across those awaits, so ShopPurchaseForgePatch
/// can attribute each send to the phantom that triggered it. Null outside a
/// phantom purchase: vanilla host-stamped sends run untouched.
/// </summary>
public static class PhantomPurchaseContext
{
    private static readonly AsyncLocal<ulong?> Current = new();

    public static ulong? Purchaser
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}
