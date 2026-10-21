using System.Reflection;
using AiBuddy.Phantom;
using AiBuddy.Policy;
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
    public enum PromptKind { Index, Indexes, CombatCards, DeckCards }

    public sealed record PendingPrompt(PromptKind Kind, IReadOnlyList<CardModel> Options, int MinSelect, int MaxSelect, bool Cancelable);

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
                PromptKind.Index => PlayerChoiceResult.FromIndex(TrivialPolicy.PickIndex(prompt.Options.Count)),
                PromptKind.Indexes => FromIndexes(prompt),
                PromptKind.CombatCards => PickCards(prompt) is { } combatCards
                    ? PlayerChoiceResult.FromMutableCombatCards(combatCards) : null,
                PromptKind.DeckCards => PickCards(prompt) is { } deckCards
                    ? PlayerChoiceResult.FromMutableDeckCards(deckCards) : null,
                _ => throw new ArgumentOutOfRangeException(),
            };
        }
        else
        {
            // A choice we have no capture for (non-card prompts, e.g. player picking).
            // KitLib precedent: default to index 0, loudly — the wait tracer surfaces hangs.
            Log.Warn($"[AiBuddy] Fabricating blind index-0 choice for phantom {player.NetId} (choiceId {choiceId}) — no prompt captured.");
            result = PlayerChoiceResult.FromIndex(0);
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

    private static PlayerChoiceResult? FromIndexes(PendingPrompt prompt)
        => TrivialPolicy.PickIndexes(prompt.Options.Count, prompt.MinSelect, prompt.MaxSelect, prompt.Cancelable)
            is { } indexes ? PlayerChoiceResult.FromIndexes(indexes) : null;

    private static IEnumerable<CardModel>? PickCards(PendingPrompt prompt)
        => TrivialPolicy.PickIndexes(prompt.Options.Count, prompt.MinSelect, prompt.MaxSelect, prompt.Cancelable)
            ?.Select(i => prompt.Options[i]);
}
