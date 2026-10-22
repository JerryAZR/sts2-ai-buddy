using System.Reflection;
using AiBuddy.Phantom;
using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace AiBuddy.Drivers;

/// <summary>
/// Fabricates mid-action player choices for phantoms.
///
/// A phantom's prompt runs the normal "remote player" path on the host:
/// ReserveChoiceId + WaitForRemoteChoice. This driver captures the prompt's
/// options (via prefix patches on the CardSelectCmd family), and when the wait
/// begins it injects a policy-computed result through the same receive path a
/// real PlayerChoiceMessage would hit. Bookkeeping (choice-id counters) stays
/// identical on every peer — critical for M7.
/// </summary>
public static class ChoiceDriver
{
    public enum PromptKind { Index, Indexes, CombatCards, DeckCards, PlayerId }

    /// <summary>What the prompt is FOR — lets the brain apply purpose-specific decisions (card rewards, removal) instead of one generic pick.</summary>
    public enum PromptPurpose { Generic, CardReward, CardRemoval }

    public sealed record PendingPrompt(PromptKind Kind, IReadOnlyList<CardModel> Options, int MinSelect, int MaxSelect, bool Cancelable)
    {
        /// <summary>Number of offered choices. Defaults to Options.Count; set explicitly for non-card index prompts (e.g. relic selects).</summary>
        public int OptionCount { get; init; } = Options.Count;
        public PromptPurpose Purpose { get; init; } = PromptPurpose.Generic;
    }

    private static readonly MethodInfo OnReceivePlayerChoice =
        typeof(PlayerChoiceSynchronizer).GetMethod("OnReceivePlayerChoice", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException("PlayerChoiceSynchronizer.OnReceivePlayerChoice");

    /// <summary>Capture prompt context for a phantom. Overwriting an unconsumed prompt means the one-outstanding-prompt invariant broke — loud, never silent.</summary>
    public static void Capture(ulong netId, PendingPrompt prompt)
    {
        if (PhantomSession.Current is not { } session || !session.IsPhantom(netId))
        {
            return;
        }
        if (session.PendingChoices.TryGetValue(netId, out PendingPrompt? unconsumed))
        {
            Log.Warn($"[AiBuddy] Phantom {netId} got a second {prompt.Kind} prompt while {unconsumed.Kind} was still unconsumed — invariant broken?");
            Observability.EventLog.Write("mod", "choice_prompt_overwritten", new { netId, previous = unconsumed.Kind.ToString() });
        }
        session.PendingChoices[netId] = prompt;
        Observability.EventLog.Write("mod", "choice_prompt_captured", new { netId, kind = prompt.Kind.ToString(), options = prompt.Options.Count });
    }

    /// <summary>
    /// Called before WaitForRemoteChoice for a phantom: fabricates the result so the
    /// wait finds it already received.
    /// </summary>
    public static void FabricateChoice(PlayerChoiceSynchronizer synchronizer, Player player, uint choiceId)
    {
        if (PhantomSession.Current is not { } session || !session.IsPhantom(player.NetId))
        {
            return;
        }

        PlayerChoiceResult? result = null;
        if (session.PendingChoices.Remove(player.NetId, out PendingPrompt? prompt))
        {
            result = prompt.Kind switch
            {
                PromptKind.Index => PlayerChoiceResult.FromIndex(prompt.Purpose == PromptPurpose.CardReward
                    ? TrivialPolicy.PickCardReward(prompt.OptionCount, player.Deck.Cards.Count)
                    : TrivialPolicy.PickIndex(prompt.OptionCount)),
                PromptKind.Indexes => FromIndexes(prompt),
                PromptKind.CombatCards => PickCards(prompt) is { } combatCards
                    ? PlayerChoiceResult.FromMutableCombatCards(combatCards) : null,
                PromptKind.DeckCards => FabricateDeckCards(player, prompt),
                PromptKind.PlayerId => PlayerChoiceResult.FromPlayerId(null), // trivial: mend nobody
                _ => throw new ArgumentOutOfRangeException(),
            };
        }
        else
        {
            // No capture for this wait: an unhandled wait site (structural gap —
            // WaitSiteCompletenessTests exists to prevent exactly this) or a stale-prompt
            // mismatch. Never guess a result shape: refuse loudly, leave the wait
            // pending, and let the wait tracer name the stall.
            Log.Error($"[AiBuddy] Phantom {player.NetId} waited on choice {choiceId} with NO captured prompt — unhandled wait site. Refusing to fabricate blindly.");
            Observability.EventLog.Write("mod", "choice_fabrication_refused", new { netId = player.NetId, choiceId, reason = "no_prompt" });
            return;
        }

        if (result == null)
        {
            // Policy refused (unsatisfiable constraints). Do NOT fabricate an invalid
            // result; leave the wait pending and make noise so the stall is diagnosable.
            Log.Warn($"[AiBuddy] No valid fabrication for phantom {player.NetId} choice {choiceId}; leaving pending.");
            Observability.EventLog.Write("mod", "choice_fabrication_refused", new { netId = player.NetId, choiceId });
            return;
        }

        Observability.EventLog.Write("mod", "choice_fabricated", new { netId = player.NetId, choiceId, result = result.ToString() });
        OnReceivePlayerChoice.Invoke(synchronizer, new object[] { player, choiceId, result.ToNetData() });
        // TODO(M7): broadcast forged-sender PlayerChoiceMessage so guests resolve their waiters too.
    }

    /// <summary>
    /// Boundary invariant: capture and consumption pair within a single command
    /// execution, so a PendingChoice must never survive a room/combat transition.
    /// A survivor means vanilla took an early-out after our capture (e.g. empty
    /// option list, auto-select shortcut) — panic and drop, never leave poison.
    /// </summary>
    public static void OnBoundary()
    {
        if (PhantomSession.Current is not { } session || session.PendingChoices.Count == 0)
        {
            return;
        }
        foreach (KeyValuePair<ulong, PendingPrompt> stale in session.PendingChoices)
        {
            Log.Error($"[AiBuddy] Phantom {stale.Key} had an unconsumed {stale.Value.Kind} prompt at a boundary (vanilla early-out after capture?) — dropping.");
            Observability.EventLog.Write("mod", "pending_choice_dropped", new { netId = stale.Key, kind = stale.Value.Kind.ToString() });
        }
        session.PendingChoices.Clear();
    }

    /// <summary>DeckCards fabrication. CardRemoval purpose uses the removal brain rule (curses → strikes → defends → don't remove); a mandatory removal with no candidate falls back to the first card, loudly.</summary>
    private static PlayerChoiceResult? FabricateDeckCards(Player player, PendingPrompt prompt)
    {
        if (prompt.Purpose == PromptPurpose.CardRemoval)
        {
            int? removal = TrivialPolicy.PickCardRemoval(prompt.Options
                .Select(c => new RemovalOption(c.Id.Entry, c.Type is CardType.Curse or CardType.Status)).ToList());
            if (removal == null && !prompt.Cancelable && prompt.Options.Count > 0)
            {
                Log.Warn($"[AiBuddy] Mandatory card removal for phantom {player.NetId} has no curse/strike/defend candidate — removing the first card.");
                removal = 0;
            }
            return PlayerChoiceResult.FromMutableDeckCards(removal is int ri ? new[] { prompt.Options[ri] } : Array.Empty<CardModel>());
        }
        return PickCards(prompt) is { } deckCards ? PlayerChoiceResult.FromMutableDeckCards(deckCards) : null;
    }

    private static PlayerChoiceResult? FromIndexes(PendingPrompt prompt)
        => TrivialPolicy.PickIndexes(prompt.Options.Count, prompt.MinSelect, prompt.MaxSelect, prompt.Cancelable)
            is { } indexes ? PlayerChoiceResult.FromIndexes(indexes) : null;

    private static IEnumerable<CardModel>? PickCards(PendingPrompt prompt)
        => TrivialPolicy.PickIndexes(prompt.Options.Count, prompt.MinSelect, prompt.MaxSelect, prompt.Cancelable)
            ?.Select(i => prompt.Options[i]);
}
