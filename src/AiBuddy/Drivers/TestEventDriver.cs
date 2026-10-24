using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;

namespace AiBuddy.Drivers;

/// <summary>
/// TESTBENCH ONLY (AIBUDDY_TEST_EVENTS="PUNCH_OFF,BATTLEWORN_DUMMY,…"):
/// front-splices specific events into the act's event set right after
/// GenerateRooms (events are consumed in order via RoomSet.NextEvent, so forced
/// events appear in the first event rooms). Works for shared events and other
/// acts' events — deterministic event coverage without playing for hours.
/// Inert without the env var; never enabled in shipped code.
/// </summary>
[HarmonyPatch(typeof(ActModel), nameof(ActModel.GenerateRooms))]
internal static class TestEventSplicePatch
{
    private static readonly FieldInfo RoomsField =
        typeof(ActModel).GetField("_rooms", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException("ActModel._rooms");

    private static void Postfix(ActModel __instance)
    {
        string? spec = System.Environment.GetEnvironmentVariable("AIBUDDY_TEST_EVENTS");
        if (string.IsNullOrWhiteSpace(spec))
        {
            return;
        }
        var rooms = (RoomSet)RoomsField.GetValue(__instance)!;
        foreach (string raw in spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string normalized = raw.ToUpperInvariant().Replace('-', '_');
            EventModel? ev = ModelDb.AllEvents.FirstOrDefault(e =>
                e.Id.Entry.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                || e.Id.Entry.Equals("EVENT." + normalized, StringComparison.OrdinalIgnoreCase));
            if (ev == null)
            {
                Log.Warn($"[AiBuddy] TEST_EVENTS: no event matches '{raw}' — skipped.");
                continue;
            }
            rooms.events.Insert(0, ev);
            Observability.EventLog.Write("mod", "test_event_spliced", new { id = ev.Id.Entry });
            Log.Info($"[AiBuddy] TEST: spliced event {ev.Id.Entry} into the act's event set.");
        }
    }
}
