using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;

namespace AiBuddy.Observability;

/// <summary>
/// Traces every point where the game starts or stops waiting on a player.
/// When something hangs, the last WAITING line names the culprit system and player.
/// Pure observation — never alters state.
/// </summary>
[HarmonyPatch]
internal static class WaitTracer
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetReadyToEndTurn))]
    private static void ReadyToEndTurn(Player player, bool canBackOut)
    {
        EventLog.Write("wait", "ready_to_end_turn", new { player = player.NetId, canBackOut });
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetReadyToBeginEnemyTurn))]
    private static void ReadyToBeginEnemyTurn(Player player)
    {
        EventLog.Write("wait", "ready_to_begin_enemy_turn", new { player = player.NetId });
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CombatStateSynchronizer), nameof(CombatStateSynchronizer.StartSync))]
    private static void CombatSyncStarted()
    {
        EventLog.Write("wait", "combat_sync_started");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CombatStateSynchronizer), "OnSyncPlayerMessageReceived")]
    private static void CombatSyncPlayerReceived(SyncPlayerDataMessage syncMessage, ulong senderId)
    {
        EventLog.Write("wait", "combat_sync_player_received", new { player = senderId });
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.SetCombatState))]
    private static void CombatStateChanged(ActionSynchronizerCombatState combatState)
    {
        EventLog.Write("wait", "combat_state", new { state = combatState.ToString() });
    }
}
