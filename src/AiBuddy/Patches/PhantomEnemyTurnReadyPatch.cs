using AiBuddy.Drivers;
using AiBuddy.Phantom;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Patches;

/// <summary>
/// After phase one of the turn-end handshake, every peer enqueues its own player's
/// ReadyToBeginEnemyTurnAction — but phantoms have no peer. When any player's
/// ReadyToBeginEnemyTurnAction executes on the host, enqueue the phantoms' too
/// (KitLib PseudoCoopReadyEnemyTurnPatch pattern).
/// </summary>
[HarmonyPatch(typeof(ReadyToBeginEnemyTurnAction), "ExecuteAction")]
internal static class PhantomEnemyTurnReadyPatch
{
    private static void Postfix()
    {
        if (PhantomSession.Current is not { } session
            || RunManager.Instance?.NetService.Type != NetGameType.Host)
        {
            return;
        }
        object? turn = CombatStateProbe.GetTurnState();
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        if (turn == null || !CombatStateProbe.IsInProgress(turn) || state == null)
        {
            return;
        }
        var ready = CombatStateProbe.PlayersReadyToBeginEnemyTurn(turn);
        foreach (PhantomDescriptor phantom in session.Phantoms)
        {
            // Dead phantoms included: in vanilla a dead player's CLIENT still sends its
            // ReadyToBeginEnemyTurnAction (the client is alive even when the character
            // isn't), and the begin-enemy-turn wait counts all players. Phantoms have
            // no client, so we send for them — dead or alive.
            Player? player = state.GetPlayer(phantom.NetId);
            if (player == null || ready.Contains(player))
            {
                continue;
            }
            Observability.EventLog.Write("mod", "phantom_enemy_turn_ready", new { netId = phantom.NetId });
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new ReadyToBeginEnemyTurnAction(player));
        }
    }
}
