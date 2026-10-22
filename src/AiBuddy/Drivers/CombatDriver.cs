using AiBuddy.Phantom;
using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Phantom combat control (design §3.5, amended): phantoms HOLD their end-turn
/// while the local host player is still playing; when the host ends their turn,
/// phantoms take their turns in registration order (one card per tick, seeing each
/// other's results), then end promptly. The "Buddy: act" button remains for manual
/// mid-turn triggers and wake-ups (e.g. after a buddy gained cards).
/// </summary>
public static class CombatDriver
{
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
        if (CombatStateProbe.PlayersReadyToEndTurn(turn).Contains(player))
        {
            session.ArmedPhantoms.Remove(player.NetId);
            return; // turn ended; only the host button (wake-up) can change that
        }

        int turnNumber = combat.TurnNumber;

        // Auto-arm once the local host player has ended their turn (buddies act after the host).
        if (!session.ArmedPhantoms.ContainsKey(player.NetId))
        {
            Player? host = LocalContext.GetMe(state);
            bool hostEnded = host != null && CombatStateProbe.PlayersReadyToEndTurn(turn).Contains(host);
            if (!hostEnded)
            {
                return; // hold: wait for the host's turn to finish
            }
            session.ArmedPhantoms[player.NetId] = turnNumber;
        }

        if (session.ArmedPhantoms[player.NetId] != turnNumber)
        {
            Enqueue(new EndPlayerTurnAction(player, turnNumber)); // stale arm from an earlier turn
            return;
        }

        // Sequencing: wait for earlier-registered armed phantoms to finish their turns.
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
                && !CombatStateProbe.PlayersReadyToEndTurn(turn).Contains(earlierPlayer))
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
        session.ArmedPhantoms.Remove(player.NetId);
        Enqueue(new EndPlayerTurnAction(player, turnNumber));
    }

    /// <summary>Host button: arm the phantom to take its turn, waking it (undo end-turn) if needed.</summary>
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
        if (CombatStateProbe.PlayersReadyToEndTurn(turn).Contains(player))
        {
            Observability.EventLog.Write("mod", "phantom_wake_up", new { netId = player.NetId, turnNumber });
            Enqueue(new UndoEndPlayerTurnAction(player, turnNumber));
        }
        session.ArmedPhantoms[player.NetId] = turnNumber;
    }

    /// <summary>Debug autopilot for the local player: play one card per tick, then end turn.</summary>
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
