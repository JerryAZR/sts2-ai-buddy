using AiBuddy.Drivers;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Patches;

/// <summary>Feeds fabricated choices into phantom prompts at the wait point.</summary>
[HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.WaitForRemoteChoice))]
internal static class ChoiceFabricationPatch
{
    private static void Prefix(PlayerChoiceSynchronizer __instance, Player player, uint choiceId)
    {
        ChoiceDriver.FabricateChoice(__instance, player, choiceId);
    }
}

/// <summary>
/// Captures prompt context (options, constraints) for phantom-owned prompts so
/// ChoiceDriver can fabricate a well-typed result. Human prompts (local or remote)
/// are ignored. Where the prompt filters the offered list internally, we replicate
/// the filter — card-identity results must reference actually-offered cards.
///
/// Completeness is enforced by WaitSiteCompletenessTests: every method whose IL
/// calls PlayerChoiceSynchronizer.WaitForRemoteChoice must have a capture here.
/// Note the capture prefix fires at method entry while vanilla may early-out before
/// the wait (empty options, auto-select shortcut) — the stale entry is dropped at
/// the next room/combat boundary (ChoiceDriver.OnBoundary).
/// </summary>
[HarmonyPatch]
internal static class CardPromptCapturePatches
{
    private static void CaptureIndexes(Player player, IEnumerable<CardModel> options, CardSelectorPrefs prefs)
        => ChoiceDriver.Capture(player.NetId, new ChoiceDriver.PendingPrompt(
            ChoiceDriver.PromptKind.Indexes, options.ToList(), prefs.MinSelect, prefs.MaxSelect, prefs.Cancelable));

    private static void CaptureDeckCards(Player player, IEnumerable<CardModel> offered, CardSelectorPrefs prefs)
        => ChoiceDriver.Capture(player.NetId, new ChoiceDriver.PendingPrompt(
            ChoiceDriver.PromptKind.DeckCards, offered.ToList(), prefs.MinSelect, prefs.MaxSelect, prefs.Cancelable));

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen))]
    private static void ChooseACard(IReadOnlyList<CardModel> cards, Player player, bool canSkip)
        => ChoiceDriver.Capture(player.NetId, new ChoiceDriver.PendingPrompt(
            ChoiceDriver.PromptKind.Index, cards, canSkip ? 0 : 1, 1, canSkip));

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGrid))]
    private static void SimpleGrid(IReadOnlyList<CardModel> cardsIn, Player player, CardSelectorPrefs prefs)
        => CaptureIndexes(player, cardsIn, prefs);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGridForRewards))]
    private static void SimpleGridForRewards(List<CardCreationResult> cards, Player player, CardSelectorPrefs prefs)
        => CaptureIndexes(player, cards.Select(c => c.Card), prefs);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseABundleScreen))]
    private static void Bundle(Player player, IReadOnlyList<IReadOnlyList<CardModel>> bundles)
        => ChoiceDriver.Capture(player.NetId, new ChoiceDriver.PendingPrompt(
            ChoiceDriver.PromptKind.Index, bundles.Select(b => b[0]).ToList(), 1, 1, false));

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromCombatPile), typeof(PlayerChoiceContext), typeof(CardPile), typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>))]
    private static void CombatPile(CardPile pile, Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter)
    {
        IEnumerable<CardModel> offered = pile.Cards;
        if (filter != null)
        {
            offered = offered.Where(filter);
        }
        ChoiceDriver.Capture(player.NetId, new ChoiceDriver.PendingPrompt(
            ChoiceDriver.PromptKind.CombatCards, offered.ToList(), prefs.MinSelect, prefs.MaxSelect, prefs.Cancelable));
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForUpgrade))]
    private static void HandForUpgrade(Player player)
        => ChoiceDriver.Capture(player.NetId, new ChoiceDriver.PendingPrompt(
            ChoiceDriver.PromptKind.CombatCards,
            PileType.Hand.GetPile(player).Cards.Where(c => c.IsUpgradable).ToList(), 1, 1, false));

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForEnchantment), typeof(IReadOnlyList<CardModel>), typeof(EnchantmentModel), typeof(int), typeof(CardSelectorPrefs))]
    private static void DeckEnchantment(IReadOnlyList<CardModel> cards, CardSelectorPrefs prefs)
    {
        // The terminal overload takes the offered cards; the choosing player is their owner.
        if (cards.Count == 0)
        {
            return;
        }
        CaptureDeckCards(cards[0].Owner, cards, prefs);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckGeneric))]
    private static void DeckGeneric(Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter)
    {
        IEnumerable<CardModel> offered = PileType.Deck.GetPile(player).Cards;
        if (filter != null)
        {
            offered = offered.Where(filter);
        }
        CaptureDeckCards(player, offered, prefs);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForUpgrade))]
    private static void DeckUpgrade(Player player, CardSelectorPrefs prefs)
        => CaptureDeckCards(player, PileType.Deck.GetPile(player).Cards.Where(c => c.IsUpgradable), prefs);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForTransformation), typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, CardTransformation>))]
    private static void DeckTransform(Player player, CardSelectorPrefs prefs)
        => CaptureDeckCards(player, PileType.Deck.GetPile(player).Cards.Where(c => c.Type != CardType.Quest && c.IsTransformable), prefs);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHand), typeof(PlayerChoiceContext), typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>), typeof(AbstractModel))]
    private static void FromHand(Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter)
    {
        IEnumerable<CardModel> offered = player.PlayerCombatState?.Hand.Cards ?? Enumerable.Empty<CardModel>();
        if (filter != null)
        {
            offered = offered.Where(filter);
        }
        ChoiceDriver.Capture(player.NetId, new ChoiceDriver.PendingPrompt(
            ChoiceDriver.PromptKind.CombatCards, offered.ToList(), prefs.MinSelect, prefs.MaxSelect, prefs.Cancelable));
    }
}

/// <summary>Relic index selects (e.g. Kaleidoscope's boss-relic pick): remote wait with an Index result.</summary>
[HarmonyPatch(typeof(RelicSelectCmd), nameof(RelicSelectCmd.FromChooseARelicScreen))]
internal static class RelicPromptCapturePatch
{
    private static void Prefix(Player player, IReadOnlyList<RelicModel> relics)
        => ChoiceDriver.Capture(player.NetId, new ChoiceDriver.PendingPrompt(
            ChoiceDriver.PromptKind.Index, Array.Empty<CardModel>(), 1, 1, false) { OptionCount = relics.Count });
}

/// <summary>Mend rest-site option: remote wait with a PlayerId result. Trivial policy mends nobody (null).</summary>
[HarmonyPatch(typeof(MendRestSiteOption), nameof(MendRestSiteOption.OnSelect))]
internal static class MendPromptCapturePatch
{
    // RestSiteOption.Owner is protected; resolve eagerly (ReflectionHolders) so EA drift fails at load.
    private static readonly System.Reflection.PropertyInfo OwnerProp =
        typeof(RestSiteOption).GetProperty("Owner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        ?? throw new MissingMemberException("RestSiteOption.Owner");

    private static void Prefix(MendRestSiteOption __instance)
        => ChoiceDriver.Capture(((Player)OwnerProp.GetValue(__instance)!).NetId, new ChoiceDriver.PendingPrompt(
            ChoiceDriver.PromptKind.PlayerId, Array.Empty<CardModel>(), 0, 1, true));
}

/// <summary>
/// Card rewards: remote wait with an IndexOrNull result (index into the offered
/// cards, or null to skip). Trivial policy takes the first card. Known limitation:
/// hooks that allow selecting MORE card rewards re-wait within one OnSelect without
/// re-entering this prefix — the second wait finds no capture and refuses (loud
/// stall) rather than guessing.
/// </summary>
[HarmonyPatch(typeof(CardReward), "OnSelect")]
internal static class CardRewardPromptCapturePatch
{
    private static void Prefix(CardReward __instance)
        => ChoiceDriver.Capture(__instance.Player.NetId, new ChoiceDriver.PendingPrompt(
            ChoiceDriver.PromptKind.Index, __instance.Cards.ToList(), 0, 1, true));
}
