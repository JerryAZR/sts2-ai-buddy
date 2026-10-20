using AiBuddy.Drivers;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
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
/// Captures prompt context (options, constraints) for phantom-owned card prompts so
/// ChoiceDriver can fabricate a well-typed result. Human prompts (local or remote)
/// are ignored. Where the prompt filters the offered list internally, we replicate
/// the filter — card-identity results must reference actually-offered cards.
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
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromCombatPile), typeof(PlayerChoiceContext), typeof(CardPile), typeof(Player), typeof(CardSelectorPrefs))]
    private static void CombatPile(CardPile pile, Player player, CardSelectorPrefs prefs)
        => ChoiceDriver.Capture(player.NetId, new ChoiceDriver.PendingPrompt(
            ChoiceDriver.PromptKind.CombatCards, pile.Cards.ToList(), prefs.MinSelect, prefs.MaxSelect, prefs.Cancelable));

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
