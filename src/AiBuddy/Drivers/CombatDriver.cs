using AiBuddy.Phantom;
using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Phantom combat control (design §3.5). Buddies HOLD their end-turn until the host
/// triggers a turn sequence — the host controls pacing entirely. All triggers go
/// through the single door <see cref="TriggerSequence"/>:
///  1. the host-only "Buddy: act" button;
///  2. the debug autopilot (triggers after ending its own turn);
///  3. the optional auto-act-on-host-end (AIBUDDY_AUTO_ACT_ON_HOST_END=1).
///
/// A trigger is a no-op while a sequence is already running — there is exactly one
/// sequence at a time, and within it buddies act strictly in registration order:
/// the current buddy is woken (UndoEndPlayerTurnAction) if it had ended, plays one
/// card per tick until exhausted, ends its turn, and only then does the next buddy
/// start. Re-triggering after a completed sequence is the intended wake-and-replay
/// path.
///
/// State is a single per-combat (index, phase) pair on the session — no per-buddy
/// stores to disagree. Dead-buddy contract: triggers never act dead buddies, and a
/// buddy that dies mid-sequence (wake in flight) is force re-ended — dead buddies
/// always end up holding.
/// </summary>
public static class CombatDriver
{
    /// <summary>Phase of the current buddy: Waking = wake enqueued, waiting for it to land; Acting = playing cards; Ending = end-turn enqueued, waiting to land.</summary>
    public enum BuddyPhase { Waking, Acting, Ending }

    private static readonly bool AutoActOnHostEnd =
        System.Environment.GetEnvironmentVariable("AIBUDDY_AUTO_ACT_ON_HOST_END") == "1";

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

    /// <summary>Per-tick sequence advance (host only). Enqueues at most one action per tick.</summary>
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
                    MegaCrit.Sts2.Core.Logging.Log.Error($"[AiBuddy] Dead buddy {player.NetId} was outside the ready-to-end set (died mid-wake?) — re-ending its turn.");
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
                    if (TrivialPolicy.PickCombatAction(player) is { } play)
                    {
                        Observability.EventLog.Write("mod", "phantom_combat_action",
                            new { netId = player.NetId, card = play.card.Id.Entry, target = play.target?.CombatId.ToString() });
                        Enqueue(new PlayCardAction(play.card, play.target));
                        return; // one card per tick; next tick re-evaluates fresh state
                    }
                    Enqueue(new EndPlayerTurnAction(player, player.PlayerCombatState.TurnNumber));
                    session.BuddySequencePhase = BuddyPhase.Ending;
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
    /// Combat-boundary reset (turn boundary = PlayPhase entry, or combat end). This is
    /// the NORMAL completion path for the final buddy: its end-turn landing satisfies
    /// all-readied, so the game leaves PlayPhase in the same frame — our 500ms tick
    /// usually never observes the sequence's last state. A genuinely stalled sequence
    /// is visible via the wait tracer (combat stuck in PlayPhase), not here.
    /// </summary>
    public static void OnCombatBoundary()
    {
        if (PhantomSession.Current is { } session && session.BuddySequenceIndex >= 0)
        {
            Observability.EventLog.Write("mod", "buddy_sequence_reset_at_boundary", new { index = session.BuddySequenceIndex });
            session.BuddySequenceIndex = -1;
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

    /// <summary>Debug autopilot for the local player: play one card per tick, then end turn, then trigger the buddies.</summary>
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
        if (TrivialPolicy.PickCombatAction(player) is { } play)
        {
            Enqueue(new PlayCardAction(play.card, play.target));
            return; // one card per tick; end turn when nothing is playable
        }
        Enqueue(new EndPlayerTurnAction(player, player.PlayerCombatState.TurnNumber));
    }

    private static void Enqueue(GameAction action)
        => RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
}
