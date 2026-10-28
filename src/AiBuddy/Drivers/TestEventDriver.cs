using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Odds;
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

    /// <summary>True when the testbench splice is active (used to bypass draw-time IsAllowed gating).</summary>
    internal static bool SpliceActive =>
        !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("AIBUDDY_TEST_EVENTS"));

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
            Observability.EventLog.Write("mod", "test_event_spliced", new { id = ev.Id.Entry,
                act = __instance.Id.Entry, head = string.Join(",", rooms.events.Take(3).Select(e => e.Id.Entry)) });
            Log.Info($"[AiBuddy] TEST: spliced event {ev.Id.Entry} into the act's event set.");
        }
    }
}

/// <summary>
/// TESTBENCH ONLY: when AIBUDDY_TEST_EVENTS is active, skip RoomSet's draw-time
/// validity gate (IsAllowed + VisitedEventIds) so spliced events actually fire —
/// e.g. CrystalSphere is act-2+/gold-gated and would always be skipped in act 1.
/// Without the env var this is fully inert.
/// </summary>
[HarmonyPatch(typeof(RoomSet), nameof(RoomSet.EnsureNextEventIsValid))]
internal static class TestEventGateBypassPatch
{
    // The ancient room (and any other pre-event consumer) advances eventsVisited —
    // snap each set's counter to the forced block the first time its gate fires.
    private static readonly HashSet<RoomSet> Snapped = new();

    private static bool Prefix(RoomSet __instance)
    {
        if (!TestEventSplicePatch.SpliceActive)
        {
            return true;
        }
        if (Snapped.Add(__instance))
        {
            __instance.eventsVisited = 0;
        }
        Observability.EventLog.Write("mod", "test_event_gate_bypassed", new
        {
            next = __instance.NextEvent.Id.Entry,
            visited = __instance.eventsVisited,
            head = string.Join(",", __instance.events.Take(3).Select(e => e.Id.Entry)),
        });
        return false;
    }
}

/// <summary>
/// TESTBENCH ONLY: with AIBUDDY_TEST_EVENTS active, Unknown (?) map points always
/// resolve to Event rooms — otherwise forcing an event still depends on the
/// seed's Unknown roll (base event odds ~85% but not deterministic).
/// </summary>
[HarmonyPatch(typeof(UnknownMapPointOdds), nameof(UnknownMapPointOdds.Roll))]
internal static class TestEventUnknownRollPatch
{
    private static void Postfix(ref RoomType __result)
    {
        if (TestEventSplicePatch.SpliceActive)
        {
            __result = RoomType.Event;
        }
    }
}
