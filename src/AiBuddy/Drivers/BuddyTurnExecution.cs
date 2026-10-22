using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;

namespace AiBuddy.Drivers;

/// <summary>A brain intent resolved to engine objects by the control layer (validated).</summary>
public sealed record ResolvedPlay(CardModel Card, Creature? Target);

/// <summary>
/// Per-player, per-turn plan-execution state (design: plan-then-drain). The driver
/// feeds the brain's batch one play at a time, waiting for the action queue to
/// settle between plays; per-play outcomes and the rejection set accumulate here and
/// go back to the brain with the next view. Recreated per player turn; dropped at
/// combat boundaries.
/// </summary>
public sealed class BuddyTurnExecution
{
    public Queue<ResolvedPlay> Pending { get; } = new();
    public List<PlayOutcome> Outcomes { get; } = new();
    /// <summary>Card ids the engine refused this turn — the executor refuses to enqueue them again.</summary>
    public HashSet<string> Rejected { get; } = new();

    /// <summary>The one play currently in the engine's queue (never more than one — settle-gated).</summary>
    public PlayCardAction? InFlight;
    public CardModel? InFlightCard;
    public MegaCrit.Sts2.Core.Entities.Creatures.Creature? InFlightTarget;
    /// <summary>Set by the PlayCardAction.CancelAction postfix — exact rejection signal.</summary>
    public bool InFlightCancelled;
    /// <summary>Settle watchdog: ticks the in-flight play has gone unsettled.</summary>
    public int TicksInFlight;
    /// <summary>Next watchdog report threshold (doubles after each report — warn-with-backoff).</summary>
    public int NextSlowReportTicks = 30; // ~15s at the 500ms tick

    public void ClearInFlight()
    {
        InFlight = null;
        InFlightCard = null;
        InFlightTarget = null;
        InFlightCancelled = false;
        TicksInFlight = 0;
        NextSlowReportTicks = 30;
    }
}
