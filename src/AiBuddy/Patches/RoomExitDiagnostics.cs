using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Patches;

/// <summary>
/// Diagnostic brackets around the room-exit chain to localize an NRE seen when
/// traveling after the ancient event with phantoms present. Debug-only.
/// </summary>
[HarmonyPatch]
internal static class RoomExitDiagnostics
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(RunManager), "ExitCurrentRooms")]
    private static void ExitRoomsBegin() => Observability.EventLog.Write("diag", "exit_rooms_begin");

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RunManager), "ExitCurrentRooms")]
    private static void ExitRoomsEnd() => Observability.EventLog.Write("diag", "exit_rooms_end");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(RunManager), "ExitCurrentRoom")]
    private static void ExitRoomBegin() => Observability.EventLog.Write("diag", "exit_room_begin");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(RewardsSetSynchronizer), "BeforeLeavingRoom")]
    private static void RewardsBeforeLeave() => Observability.EventLog.Write("diag", "rewards_before_leave");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(EventRoom), nameof(EventRoom.Exit))]
    private static void EventExitBegin()
    {
        var sync = RunManager.Instance.EventSynchronizer;
        Observability.EventLog.Write("diag", "event_exit_begin", new
        {
            events = sync.Events.Count,
            finished = sync.Events.Count(e => e.IsFinished),
        });
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(EventRoom), nameof(EventRoom.Exit))]
    private static void EventExitEnd() => Observability.EventLog.Write("diag", "event_exit_end");
}
