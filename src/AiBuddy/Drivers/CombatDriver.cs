using AiBuddy.Phantom;
using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Phantom combat control (design §3.5). Buddies HOLD their end-turn while unarmed —
/// the host controls turn pacing entirely. Triggers:
///  1. the host-only "Buddy: act" button (arms all buddies; wakes ended ones);
///  2. optional auto-act-on-host-end (AIBUDDY_AUTO_ACT_ON_HOST_END=1 for now);
///  3. the debug autopilot, which triggers after ending its own turn.
/// Armed buddies act in registration order — buddy N+1 waits for buddy N to end
/// its turn, so each sees the previous one's results. One card per tick (fresh
/// state per pick). An exhausted buddy disarms and ends its turn. Re-triggering an
/// ended buddy wakes it via the vanilla UndoEndPlayerTurnAction.
/// </summary>
public static class CombatDriver
{
    private static readonly bool AutoActOnHostEnd =
        System.Environment.GetEnvironmentVariable("AIBUDDY_AUTO_ACT_ON_HOST_END") == "1";

    private static bool InPlayPhase =>
        RunManager.Instance.ActionQueueSynchronizer.CombatState == ActionSynchronizerCombatState.PlayPhase;

    /// <summary>Per-tick phantom combat driving (host only).</summary>
    public static void Drive(Player player, RunState state)
    {
        if (PhantomSession.Current is not { } session || !InPlayPhase)
        {
            return;
        }
        object? turn = CombatStateProbe.GetTurnState();
        PlayerCombatState? combat = player.PlayerCombatState;
        if (turn == null || !CombatStateProbe.IsInProgress(turn) || combat == null || player.Creature.IsDead)
        {
            return;
        }
        var readyToEnd = CombatStateProbe.PlayersReadyToEndTurn(turn);
        if (readyToEnd.Contains(player))
        {
            session.ArmedPhantoms.Remove(player.NetId);
            return; // turn ended; only a trigger (wake-up) can change that
        }
        session.PendingWakeUps.Remove(player.NetId); // wake-up (if any) has landed

        int turnNumber = combat.TurnNumber;

        // Arms are per-turn: a stale arm from an earlier turn never carries over.
        if (session.ArmedPhantoms.TryGetValue(player.NetId, out int armedTurn) && armedTurn != turnNumber)
        {
            session.ArmedPhantoms.Remove(player.NetId);
        }

        // Optional trigger: auto-act when the local host player ends their turn.
        // Same door as the button and the autopilot: ArmOrWake owns arming rules.
        if (AutoActOnHostEnd && !session.ArmedPhantoms.ContainsKey(player.NetId))
        {
            Player? host = MegaCrit.Sts2.Core.Context.LocalContext.GetMe(state);
            if (host != null && readyToEnd.Contains(host))
            {
                ArmOrWake(player);
            }
        }

        // Unarmed buddies hold — the host decides when (and whether) they act.
        if (!session.ArmedPhantoms.ContainsKey(player.NetId))
        {
            return;
        }

        // Sequencing: wait for earlier-registered armed buddies to finish their turns.
        foreach (PhantomDescriptor earlier in session.Phantoms)
        {
            if (earlier.NetId == player.NetId)
            {
                break;
            }
            Player? earlierPlayer = state.GetPlayer(earlier.NetId);
            if (earlierPlayer != null
                && session.ArmedPhantoms.ContainsKey(earlier.NetId)
                && !earlierPlayer.Creature.IsDead
                && !readyToEnd.Contains(earlierPlayer))
            {
                return; // earlier buddy is still taking its turn
            }
        }

        if (TrivialPolicy.PickCombatAction(player) is { } play)
        {
            Observability.EventLog.Write("mod", "phantom_combat_action",
                new { netId = player.NetId, card = play.card.Id.Entry, target = play.target?.CombatId.ToString() });
            Enqueue(new PlayCardAction(play.card, play.target));
            return; // stay armed; next tick re-evaluates fresh state
        }

        // Nothing playable: disarm and end the turn.
        // Known benign race: a re-trigger landing while this EndPlayerTurnAction is
        // still queued plays the new cards AFTER the end-turn commit (queue order).
        // The buddy ends its turn having played them — acceptable, no stall.
        session.ArmedPhantoms.Remove(player.NetId);
        Enqueue(new EndPlayerTurnAction(player, turnNumber));
    }

    /// <summary>
    /// The single door for all triggers (button, autopilot, auto-act option).
    /// Guards: no-op if the buddy is already armed this turn; no duplicate wake
    /// while one is in flight; arms are per-turn. A trigger arriving after all
    /// buddies ended is the intended wake-and-replay path (design §3.5 point 4).
    /// </summary>
    public static void ArmOrWake(Player player)
    {
        if (PhantomSession.Current is not { } session)
        {
            return;
        }
        object? turn = CombatStateProbe.GetTurnState();
        if (turn == null || !CombatStateProbe.IsInProgress(turn) || player.PlayerCombatState == null)
        {
            return;
        }
        int turnNumber = player.PlayerCombatState.TurnNumber;
        if (session.ArmedPhantoms.TryGetValue(player.NetId, out int existing) && existing == turnNumber)
        {
            return; // already taking its turn — re-trigger mid-turn is a no-op
        }
        var readyToEnd = CombatStateProbe.PlayersReadyToEndTurn(turn);
        if (readyToEnd.Contains(player))
        {
            if (!session.PendingWakeUps.Add(player.NetId))
            {
                return; // a wake-up is already in flight (e.g. button + autopilot both triggered)
            }
            Observability.EventLog.Write("mod", "phantom_wake_up", new { netId = player.NetId, turnNumber });
            Enqueue(new UndoEndPlayerTurnAction(player, turnNumber));
        }
        session.ArmedPhantoms[player.NetId] = turnNumber;
    }

    /// <summary>Debug autopilot for the local player: play one card per tick, then end turn, then trigger buddies.</summary>
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
        var readyToEnd = CombatStateProbe.PlayersReadyToEndTurn(turn);
        if (readyToEnd.Contains(player))
        {
            // Autopilot trigger: buddies act after the host's own turn has ended.
            if (PhantomSession.Current is { } session && state.Players.Any(p => session.IsPhantom(p.NetId)))
            {
                foreach (PhantomDescriptor phantom in session.Phantoms)
                {
                    Player? buddy = state.GetPlayer(phantom.NetId);
                    if (buddy != null
                        && !buddy.Creature.IsDead
                        && !readyToEnd.Contains(buddy)
                        && !session.ArmedPhantoms.ContainsKey(buddy.NetId))
                    {
                        ArmOrWake(buddy);
                    }
                }
            }
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
