using System.Reflection;
using AiBuddy.Phantom;
using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Phantom combat control (design §3.5). The control layer owns mechanical
/// confirmations: a phantom ends its turn promptly unless the host armed it this
/// turn via the "Buddy: act" button. Wake-up after an ended turn = the vanilla
/// UndoEndPlayerTurnAction. Enemy-turn readiness is driven by a postfix on
/// ReadyToBeginEnemyTurnAction (KitLib pattern): when any player's executes,
/// the phantoms' are enqueued too.
/// </summary>
public static class CombatDriver
{
    private static readonly bool ArmForTesting =
        System.Environment.GetEnvironmentVariable("AIBUDDY_ARM_PHANTOMS") == "1";

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
            return; // turn ended; only the host button (wake-up) can change that
        }

        // Test harness: AIBUDDY_ARM_PHANTOMS=1 arms every phantom every turn,
        // the headless equivalent of the host pressing "Buddy: act".
        if (ArmForTesting)
        {
            session.ArmedPhantoms[player.NetId] = combat.TurnNumber;
        }
        if (CombatStateProbe.PlayersReadyToEndTurn(turn).Contains(player))
        {
            return; // turn ended; only the host button (wake-up) can change that
        }

        int turnNumber = combat.TurnNumber;
        if (session.ArmedPhantoms.TryGetValue(player.NetId, out int armedTurn) && armedTurn == turnNumber)
        {
            session.ArmedPhantoms.Remove(player.NetId);
            if (TrivialPolicy.PickCombatAction(player) is { } play)
            {
                Observability.EventLog.Write("mod", "phantom_combat_action",
                    new { netId = player.NetId, card = play.card.Id.Entry, target = play.target?.CombatId.ToString() });
                Enqueue(new PlayCardAction(play.card, play.target));
            }
            return; // let the play execute; the turn ends on a later tick
        }

        Enqueue(new EndPlayerTurnAction(player, turnNumber));
    }

    /// <summary>Host button: arm the phantom to act this turn, waking it (undo end-turn) if needed.</summary>
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
        object? turnAuto = CombatStateProbe.GetTurnState();
        if (turnAuto == null || !CombatStateProbe.IsInProgress(turnAuto) || player.PlayerCombatState == null || player.Creature.IsDead)
        {
            return;
        }
        if (CombatStateProbe.PlayersReadyToEndTurn(turnAuto).Contains(player))
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
