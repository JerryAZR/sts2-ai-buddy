using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace AiBuddy.Drivers;

/// <summary>
/// ICardSelector answering the LOCAL player's card prompts with trivial-policy
/// defaults. Installed (localOnly) by the debug autopilot only — never for phantoms,
/// whose prompts must take the remote path to keep choice-id bookkeeping identical
/// on every peer. Same seam AutoSlay uses (AutoSlayCardSelector).
/// </summary>
public sealed class AutoplayCardSelector : ICardSelector
{
    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        List<CardModel> list = options.ToList();
        // Shared policy; on refusal (unsatisfiable), pick nothing rather than emit an invalid selection.
        List<int> picked = Policy.TrivialPolicy.PickIndexes(list.Count, minSelect, maxSelect, cancelable: false) ?? new List<int>();
        IEnumerable<CardModel> selected = picked.Select(i => list[i]).ToList();
        Observability.EventLog.Write("mod", "autopilot_card_select", new { options = list.Count, picked.Count });
        return Task.FromResult(selected);
    }

    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
    {
        // Autopilot takes the first offered card (reward flow coverage beats strategy here).
        return new CardRewardSelection { card = options.FirstOrDefault()?.Card };
    }
}
