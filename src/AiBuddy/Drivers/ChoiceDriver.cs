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

    /// <summary>Captured prompt context per phantom NetId. FIFO intent: one outstanding prompt per player.</summary>
    private static readonly Dictionary<ulong, PendingPrompt> _pending = new();

    private static readonly MethodInfo OnReceivePlayerChoice =
        typeof(PlayerChoiceSynchronizer).GetMethod("OnReceivePlayerChoice", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException("PlayerChoiceSynchronizer.OnReceivePlayerChoice");

    public static void Capture(ulong netId, PendingPrompt prompt)
    {
        if (!PhantomRegistry.IsPhantom(netId))
        {
            return;
        }
        _pending[netId] = prompt;
        Observability.EventLog.Write("mod", "choice_prompt_captured", new { netId, kind = prompt.Kind.ToString(), options = prompt.Options.Count });
    }

    /// <summary>
    /// Called before WaitForRemoteChoice for a phantom: fabricates the result so the
    /// wait finds it already received. Returns false if there was nothing to fabricate.
    /// </summary>
    public static bool FabricateChoice(PlayerChoiceSynchronizer synchronizer, Player player, uint choiceId)
    {
        if (!PhantomRegistry.IsPhantom(player.NetId))
        {
            return false;
        }

        PlayerChoiceResult result;
        if (_pending.Remove(player.NetId, out PendingPrompt? prompt))
        {
            result = prompt.Kind switch
            {
                PromptKind.Index => PlayerChoiceResult.FromIndex(TrivialPolicy.PickIndex(prompt.Options.Count)),
                PromptKind.Indexes => PlayerChoiceResult.FromIndexes(
                    TrivialPolicy.PickIndexes(prompt.Options.Count, prompt.MinSelect, prompt.MaxSelect, prompt.Cancelable)),
                PromptKind.CombatCards => PlayerChoiceResult.FromMutableCombatCards(
                    TrivialPolicy.PickIndexes(prompt.Options.Count, prompt.MinSelect, prompt.MaxSelect, prompt.Cancelable)
                        .Select(i => prompt.Options[i])),
                PromptKind.DeckCards => PlayerChoiceResult.FromMutableDeckCards(
                    TrivialPolicy.PickIndexes(prompt.Options.Count, prompt.MinSelect, prompt.MaxSelect, prompt.Cancelable)
                        .Select(i => prompt.Options[i])),
                _ => throw new ArgumentOutOfRangeException(),
            };
        }
        else
        {
            // A choice we don't have a capture for (non-card prompts, e.g. player picking).
            // KitLib precedent: default to index 0, loudly — the wait tracer will surface hangs.
            Log.Warn($"[AiBuddy] Fabricating blind index-0 choice for phantom {player.NetId} (choiceId {choiceId}) — no prompt captured.");
            result = PlayerChoiceResult.FromIndex(0);
        }

        Observability.EventLog.Write("mod", "choice_fabricated", new { netId = player.NetId, choiceId, result = result.ToString() });
        OnReceivePlayerChoice.Invoke(synchronizer, new object[] { player, choiceId, result.ToNetData() });
        // TODO(M7): broadcast forged-sender PlayerChoiceMessage so guests resolve their waiters too.
        return true;
    }
}
