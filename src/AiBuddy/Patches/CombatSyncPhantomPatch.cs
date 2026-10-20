using System.Reflection;
using AiBuddy.Phantom;
using AiBuddy.Observability;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AiBuddy.Patches;

/// <summary>
/// Fabricates phantom sync payloads at room entry. CombatStateSynchronizer waits for
/// every RunLobby member's SyncPlayerDataMessage before entering a room; phantoms never
/// send one, so the host injects the phantom's own authoritative Player serialization
/// into the synchronizer and re-triggers completion checks.
///
/// Guests are out of scope until M7: that will additionally need a forged-sender
/// SyncPlayerDataMessage broadcast (a normal broadcast would arrive AS THE HOST and be
/// discarded as a duplicate). Loopback sessions have no guests, so injection suffices.
/// </summary>
[HarmonyPatch(typeof(CombatStateSynchronizer), nameof(CombatStateSynchronizer.StartSync))]
internal static class CombatSyncPhantomPatch
{
    private static readonly FieldInfo SyncDataField =
        typeof(CombatStateSynchronizer).GetField("_syncData", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException("CombatStateSynchronizer._syncData");

    private static readonly FieldInfo RunLobbyField =
        typeof(CombatStateSynchronizer).GetField("_runLobby", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException("CombatStateSynchronizer._runLobby");

    private static readonly FieldInfo RunStateField =
        typeof(CombatStateSynchronizer).GetField("_runState", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException("CombatStateSynchronizer._runState");

    private static readonly MethodInfo CheckCompleted =
        typeof(CombatStateSynchronizer).GetMethod("CheckSyncCompleted", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException("CombatStateSynchronizer.CheckSyncCompleted");

    private static void Postfix(CombatStateSynchronizer __instance, INetGameService ____netService)
    {
        if (____netService.Type != NetGameType.Host || __instance.IsDisabled || PhantomRegistry.All.Count == 0)
        {
            return;
        }
        var syncData = (Dictionary<ulong, SerializablePlayer>)SyncDataField.GetValue(__instance)!;
        var runState = (RunState)RunStateField.GetValue(__instance)!;

        foreach (PhantomDescriptor phantom in PhantomRegistry.All)
        {
            Player? player = runState.GetPlayer(phantom.NetId);
            if (player == null)
            {
                EventLog.Write("mod", "sync_inject_missing_player", new { netId = phantom.NetId });
                continue;
            }
            syncData[phantom.NetId] = player.ToSerializable();
            EventLog.Write("mod", "sync_injected", new { netId = phantom.NetId });
        }
        CheckCompleted.Invoke(__instance, null);
    }
}
