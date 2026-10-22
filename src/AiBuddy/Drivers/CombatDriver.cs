using AiBuddy.Phantom;
using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Phantom combat control (design §3.5) — plan-then-drain executor.
///
/// Buddies HOLD their end-turn until the host triggers a turn sequence — the host
/// controls pacing entirely. All triggers go through the single door
/// <see cref="TriggerSequence"/> (button, debug autopilot, optional
/// AIBUDDY_AUTO_ACT_ON_HOST_END). One sequence at a time; buddies act strictly in
/// registration order; each buddy's turn is driven by the BRAIN (TrivialPolicy):
/// the executor asks for an intent (PlayBatch or EndTurn), feeds plays one at a
/// time waiting for the action queue to settle, records per-play outcomes, and
/// kills the rest of a batch on the first rejection. Turn-end is the brain's
/// decision, never the driver's.
///
/// Dead-buddy contract: triggers never act dead buddies, and a buddy that dies
/// mid-sequence is force re-ended — dead buddies always end up holding.
/// </summary>
public static class CombatDriver
{
    /// <summary>Phase of the current buddy: Waking = wake enqueued, waiting for it to land; Acting = brain-driven plays; Ending = end-turn enqueued, waiting to land.</summary>
    public enum BuddyPhase { Waking, Acting, Ending }

    private static readonly bool AutoActOnHostEnd =
        System.Environment.GetEnvironmentVariable("AIBUDDY_AUTO_ACT_ON_HOST_END") == "1";

    /// <summary>Debug autopilot turn state for the local player (debug-only; phantoms use session.CurrentBuddyTurn).</summary>
    private static BuddyTurnExecution? _autopilotTurn;

    private static bool InPlayPhase =>
        RunManager.Instance.ActionQueueSynchronizer.CombatState == ActionSynchronizerCombatState.PlayPhase;

    /// <summary>
    /// The single door for all triggers. No-op while a sequence is running (one
    /// sequence at a time) or outside the play phase (the turn-end handshake cancels
    /// or defers player-driven actions — a trigger there would be silently swallowed).
    /// </summary>
    public static void TriggerSequence(RunState state)
    {
        if (PhantomSession.Current is not { } session || session.BuddySequenceIndex >= 0)
        {
            return; // a sequence is already running — triggers never stack
        }
        if (!InPlayPhase)
        {
            Observability.EventLog.Write("mod", "buddy_trigger_ignored", new { reason = "not_play_phase" });
            return;
        }
        object? turn = CombatStateProbe.GetTurnState();
        if (turn == null || !CombatStateProbe.IsInProgress(turn) || session.Phantoms.Count == 0)
        {
            return;
        }
        Observability.EventLog.Write("mod", "buddy_sequence_triggered");
        session.BuddySequenceIndex = 0;
        EnterCurrentBuddy(session, state, turn);
    }

    /// <summary>Per-tick sequence advance (host only). Enqueues at most one engine action per tick.</summary>
    public static void DriveSequence(RunState state)
    {
        if (PhantomSession.Current is not { } session || !InPlayPhase)
        {
            return;
        }
        object? turn = CombatStateProbe.GetTurnState();
        if (turn == null || !CombatStateProbe.IsInProgress(turn))
        {
            return;
        }

        // Optional trigger: auto-act when the local host player ends their turn. Same door.
        if (AutoActOnHostEnd && session.BuddySequenceIndex < 0)
        {
            Player? host = MegaCrit.Sts2.Core.Context.LocalContext.GetMe(state);
            if (host != null && CombatStateProbe.PlayersReadyToEndTurn(turn).Contains(host))
            {
                TriggerSequence(state);
            }
        }
        if (session.BuddySequenceIndex < 0)
        {
            return;
        }

        var readyToEnd = CombatStateProbe.PlayersReadyToEndTurn(turn);
        int hops = 0;
        while (session.BuddySequenceIndex < session.Phantoms.Count && ++hops <= session.Phantoms.Count + 2)
        {
            PhantomDescriptor buddy = session.Phantoms[session.BuddySequenceIndex];
            Player? player = state.GetPlayer(buddy.NetId);
            if (player?.PlayerCombatState == null)
            {
                Advance(session, state, turn);
                continue;
            }
            bool readied = readyToEnd.Contains(player);

            if (player.Creature.IsDead)
            {
                if (readied)
                {
                    Advance(session, state, turn); // dead buddies hold end-turn; skip
                    continue;
                }
                // Dead but un-readied: the died-mid-wake race. Force the end-turn back —
                // nothing else will, and an un-readied player blocks the turn forever.
                if (session.DeadBuddyPanicLogged.Add(player.NetId))
                {
                    Log.Error($"[AiBuddy] Dead buddy {player.NetId} was outside the ready-to-end set (died mid-wake?) — re-ending its turn.");
                }
                Observability.EventLog.Write("mod", "dead_buddy_repinned", new { netId = player.NetId });
                Enqueue(new EndPlayerTurnAction(player, player.PlayerCombatState.TurnNumber));
                return;
            }

            switch (session.BuddySequencePhase)
            {
                case BuddyPhase.Waking:
                    if (readied)
                    {
                        return; // wake still in flight
                    }
                    session.BuddySequencePhase = BuddyPhase.Acting;
                    continue;

                case BuddyPhase.Acting:
                    session.CurrentBuddyTurn ??= new BuddyTurnExecution();
                    if (DrivePlayerTurn(player, session.CurrentBuddyTurn))
                    {
                        session.CurrentBuddyTurn = null; // brain ended the turn
                        session.BuddySequencePhase = BuddyPhase.Ending;
                    }
                    return;

                case BuddyPhase.Ending:
                    if (!readied)
                    {
                        return; // end-turn still in flight
                    }
                    Advance(session, state, turn);
                    continue;
            }
        }

        if (session.BuddySequenceIndex >= session.Phantoms.Count)
        {
            session.BuddySequenceIndex = -1;
            Observability.EventLog.Write("mod", "buddy_sequence_done");
        }
    }

    /// <summary>
    /// One tick of one player's brain-driven turn. Returns true when end-turn was
    /// enqueued. Settle-gated: never enqueues a play while the action queue is
    /// non-empty, so the queue can never pile up with one player's plans.
    /// </summary>
    private static bool DrivePlayerTurn(Player player, BuddyTurnExecution exec)
    {
        ActionQueueSet queue = RunManager.Instance.ActionQueueSet;

        // Resolve the in-flight play once the queue has fully settled.
        if (exec.InFlight != null)
        {
            if (!queue.IsEmpty)
            {
                // Settle watchdog (warn with backoff): a play that never settles means
                // the engine is waiting on something we can't see — diagnose, don't panic
                // (legitimate long combos look identical from the outside).
                if (++exec.TicksInFlight >= exec.NextSlowReportTicks)
                {
                    Log.Warn($"[AiBuddy] Play of {exec.InFlightCard?.Id.Entry} by {player.NetId} unsettled after ~{exec.TicksInFlight / 2}s " +
                             $"(queuePaused={queue.ActionQueueIsPaused(player.NetId)}). Slow combo or hang — wait tracer has details.");
                    Observability.EventLog.Write("mod", "play_settle_slow", new
                    {
                        netId = player.NetId,
                        card = exec.InFlightCard?.Id.Entry,
                        ticksInFlight = exec.TicksInFlight,
                        queuePaused = queue.ActionQueueIsPaused(player.NetId),
                    });
                    exec.NextSlowReportTicks *= 2;
                }
                return false;
            }

            CardModel card = exec.InFlightCard!;
            bool inHand = card.Pile?.Type == PileType.Hand;
            bool success = !exec.InFlightCancelled && !inHand;
            string? reason = null;
            if (!success)
            {
                reason = DiagnoseRejection(exec);
                exec.Rejected.Add(card);
                if (exec.Pending.Count > 0)
                {
                    // Kill-chain: the world diverged from the batch's planning assumptions.
                    Observability.EventLog.Write("mod", "play_batch_killed", new { netId = player.NetId, dropped = exec.Pending.Count, rejectedCard = card.Id.Entry });
                    exec.Pending.Clear();
                }
            }
            exec.Outcomes.Add(new PlayOutcome(card, success, reason));
            Observability.EventLog.Write("mod", "play_outcome", new { netId = player.NetId, card = card.Id.Entry, success, reason });
            exec.ClearInFlight();
        }

        // Feed the next planned play.
        if (exec.Pending.Count > 0)
        {
            PlannedPlay play = exec.Pending.Dequeue();
            if (exec.Rejected.Contains(play.Card))
            {
                Log.Warn($"[AiBuddy] Brain re-proposed rejected card {play.Card.Id.Entry} for {player.NetId} — refusing (brain contract violation).");
                Observability.EventLog.Write("mod", "brain_contract_violation", new { netId = player.NetId, card = play.Card.Id.Entry });
                exec.Pending.Clear();
            }
            else
            {
                EnqueuePlay(player, exec, play);
                return false;
            }
        }

        // Batch done (or none): hand fresh state + outcomes back to the brain.
        BuddyIntent intent = TrivialPolicy.PlanTurnIntent(new BuddyTurnView(player, exec.Rejected, exec.Outcomes));
        exec.Outcomes.Clear(); // consumed by the brain
        if (intent is EndTurnIntent)
        {
            Enqueue(new EndPlayerTurnAction(player, player.PlayerCombatState!.TurnNumber));
            return true;
        }
        if (intent is PlayBatch batch)
        {
            foreach (PlannedPlay play in batch.Plays)
            {
                if (exec.Rejected.Contains(play.Card))
                {
                    Log.Warn($"[AiBuddy] Brain proposed rejected card {play.Card.Id.Entry} for {player.NetId} — refusing (brain contract violation).");
                    Observability.EventLog.Write("mod", "brain_contract_violation", new { netId = player.NetId, card = play.Card.Id.Entry });
                    continue;
                }
                exec.Pending.Enqueue(play);
            }
            if (exec.Pending.Count > 0)
            {
                EnqueuePlay(player, exec, exec.Pending.Dequeue());
                return false;
            }
        }
        // Brain proposed nothing playable → end the turn.
        Enqueue(new EndPlayerTurnAction(player, player.PlayerCombatState!.TurnNumber));
        return true;
    }

    private static void EnqueuePlay(Player player, BuddyTurnExecution exec, PlannedPlay play)
    {
        var action = new PlayCardAction(play.Card, play.Target);
        exec.InFlight = action;
        exec.InFlightCard = play.Card;
        exec.InFlightTarget = play.Target;
        Observability.EventLog.Write("mod", "phantom_combat_action",
            new { netId = player.NetId, card = play.Card.Id.Entry, target = play.Target?.CombatId.ToString() });
        Enqueue(action);
    }

    /// <summary>Why did the engine refuse this play? Re-evaluated at settle time (pure queries).</summary>
    private static string DiagnoseRejection(BuddyTurnExecution exec)
    {
        CardModel card = exec.InFlightCard!;
        if (!card.CanPlay(out UnplayableReason reason, out _))
        {
            return $"unplayable:{reason}";
        }
        if (exec.InFlightTarget != null && !card.IsValidTarget(exec.InFlightTarget))
        {
            return "invalid_target";
        }
        return exec.InFlightCancelled ? "cancelled" : "card_remained_in_hand";
    }

    /// <summary>Exact rejection signal: PlayCardAction.CancelAction postfix routes here.</summary>
    internal static void NotifyPlayCancelled(PlayCardAction action)
    {
        if (PhantomSession.Current?.CurrentBuddyTurn?.InFlight == action)
        {
            PhantomSession.Current.CurrentBuddyTurn.InFlightCancelled = true;
        }
        if (_autopilotTurn?.InFlight == action)
        {
            _autopilotTurn.InFlightCancelled = true;
        }
    }

    /// <summary>
    /// Combat-boundary reset (turn boundary = PlayPhase entry, or combat end). This is
    /// the NORMAL completion path for the final buddy: its end-turn landing satisfies
    /// all-readied, so the game leaves PlayPhase in the same frame — our 500ms tick
    /// usually never observes the sequence's last state. A genuinely stalled sequence
    /// is visible via the wait tracer (combat stuck in PlayPhase), not here.
    /// </summary>
    public static void OnCombatBoundary()
    {
        _autopilotTurn = null;
        if (PhantomSession.Current is { } session && session.BuddySequenceIndex >= 0)
        {
            Observability.EventLog.Write("mod", "buddy_sequence_reset_at_boundary", new { index = session.BuddySequenceIndex });
            session.BuddySequenceIndex = -1;
            session.CurrentBuddyTurn = null;
        }
    }

    /// <summary>Debug autopilot for the local player — the same brain + executor the buddies use, plus the trigger after ending its own turn.</summary>
    public static void DriveAutoplay(Player player, RunState state)
    {
        if (!InPlayPhase)
        {
            return;
        }
        object? turn = CombatStateProbe.GetTurnState();
        if (turn == null || !CombatStateProbe.IsInProgress(turn) || player.PlayerCombatState == null || player.Creature.IsDead)
        {
            return;
        }
        if (CombatStateProbe.PlayersReadyToEndTurn(turn).Contains(player))
        {
            // Autopilot trigger: buddies act after the host's own turn has ended. Same door.
            TriggerSequence(state);
            return;
        }
        _autopilotTurn ??= new BuddyTurnExecution();
        if (DrivePlayerTurn(player, _autopilotTurn))
        {
            _autopilotTurn = null;
        }
    }

    private static void Advance(PhantomSession session, RunState state, object turn)
    {
        session.BuddySequenceIndex++;
        EnterCurrentBuddy(session, state, turn);
    }

    /// <summary>Enter the current buddy: wake it if it already ended (wake lands next tick); otherwise it starts acting. Dead/self-resolving buddies are handled by the drive loop.</summary>
    private static void EnterCurrentBuddy(PhantomSession session, RunState state, object turn)
    {
        session.BuddySequencePhase = BuddyPhase.Waking;
        session.CurrentBuddyTurn = null;
        if (session.BuddySequenceIndex >= session.Phantoms.Count)
        {
            return;
        }
        Player? player = state.GetPlayer(session.Phantoms[session.BuddySequenceIndex].NetId);
        if (player?.PlayerCombatState == null || player.Creature.IsDead)
        {
            return;
        }
        if (CombatStateProbe.PlayersReadyToEndTurn(turn).Contains(player))
        {
            Observability.EventLog.Write("mod", "phantom_wake_up", new { netId = player.NetId, turnNumber = player.PlayerCombatState.TurnNumber });
            Enqueue(new UndoEndPlayerTurnAction(player, player.PlayerCombatState.TurnNumber));
        }
        else
        {
            session.BuddySequencePhase = BuddyPhase.Acting; // never ended — straight to acting
        }
    }

    private static void Enqueue(GameAction action)
        => RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
}
