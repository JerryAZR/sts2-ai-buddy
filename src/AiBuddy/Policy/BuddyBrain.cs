using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace AiBuddy.Policy;

/// <summary>
/// The brain contract (design §3.5). A brain observes a turn view and returns an
/// intent — an ordered batch of plays, or end-turn. It NEVER actuates, and it never
/// sees mid-animation state: the executor feeds one play at a time, waiting for the
/// action queue to fully settle between plays, and comes back with fresh state plus
/// per-play outcomes when the batch finishes or is killed.
///
/// Batch-of-1 IS the play-and-see agent — no special casing anywhere. Turn-end is
/// the brain's decision, never the driver's.
/// </summary>
public abstract record BuddyIntent;

/// <summary>Play these cards, in order. Kill-chain semantics: the first rejected play aborts the rest of the batch (the world diverged from planning assumptions).</summary>
public sealed record PlayBatch(IReadOnlyList<PlannedPlay> Plays) : BuddyIntent;

/// <summary>The brain is done this turn.</summary>
public sealed record EndTurnIntent : BuddyIntent
{
    public static readonly EndTurnIntent Instance = new();
}

/// <summary>One intended play. Target must satisfy the card's TargetType (enemy creature, ally creature, or null for untargeted).</summary>
public sealed record PlannedPlay(CardModel Card, Creature? Target);

/// <summary>What happened to one executed play — feedback for the brain.</summary>
public sealed record PlayOutcome(CardModel Card, bool Success, string? RejectionReason);

/// <summary>
/// What the brain sees. RejectedThisTurn: cards the engine refused this turn — the
/// executor will REFUSE to enqueue them again, so re-proposing one is a brain
/// contract violation (logged). LastOutcomes: results of the previous batch since
/// the brain was last asked.
/// </summary>
public sealed record BuddyTurnView(Player Player, IReadOnlySet<CardModel> RejectedThisTurn, IReadOnlyList<PlayOutcome> LastOutcomes);
