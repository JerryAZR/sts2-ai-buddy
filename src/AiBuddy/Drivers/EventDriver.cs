using AiBuddy.Policy;
using System.Reflection;

using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Drives event decisions one step per call. Decision logic is shared policy;
/// only actuation differs: the local player goes through ChooseLocalOption (the
/// same method the UI calls), phantoms through host-side execution of their event
/// instance (ChooseOptionForEvent — the handler remote clients' messages trigger).
/// </summary>
public static class EventDriver
{
    private static readonly MethodInfo ChooseOptionForEvent =
        typeof(EventSynchronizer).GetMethod("ChooseOptionForEvent", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException("EventSynchronizer.ChooseOptionForEvent");

    private static readonly HashSet<EventModel> _proceededFor = new();

    /// <summary>Drive the given player's current event one step, if there is a stable page with a valid option.</summary>
    public static void Drive(Player player, RunState state)
    {
        EventSynchronizer sync = RunManager.Instance.EventSynchronizer;
        if (sync.Events.Count == 0 || sync.IsShared)
        {
            // Shared/collaboration events are M4 (vote mirroring).
            return;
        }
        EventModel? ev = sync.Events.FirstOrDefault(e => e.Owner == player);
        if (ev == null)
        {
            return;
        }
        if (ev.IsFinished)
        {
            // Proceed is a local-UI concept (opens the map); phantoms have no screen,
            // so only the autopilot needs it. Guard on the CURRENT room being this
            // event's room: finished events linger in the synchronizer after exit,
            // and re-proceeding from a stale event force-opens the map mid-room.
            if (LocalContext.IsMe(player)
                && state.CurrentRoom is EventRoom
                && _proceededFor.Add(ev))
            {
                Observability.EventLog.Write("mod", "autopilot_event_proceed", new { eventId = ev.Id.Entry });
                TaskHelper.RunSafely(NEventRoom.Proceed());
            }
            return;
        }
        // Page transition guard: an option was chosen but its async task hasn't
        // advanced the page yet — wait for the next tick.
        if (ev.CurrentOptions.Any(o => o.WasChosen))
        {
            return;
        }
        if (TrivialPolicy.PickEventOption(ev) is not int index)
        {
            return;
        }

        bool isLocal = LocalContext.IsMe(player);
        Observability.EventLog.Write("mod", isLocal ? "autopilot_event_choice" : "phantom_event_choice", new
        {
            netId = player.NetId,
            eventId = ev.Id.Entry,
            index,
            option = ev.CurrentOptions[index].TextKey,
        });

        if (isLocal)
        {
            sync.ChooseLocalOption(index);
        }
        else
        {
            ChooseOptionForEvent.Invoke(sync, new object[] { player, index });
            // TODO(M7): broadcast forged-sender OptionIndexChosenMessage so guests
            // re-execute the phantom's event choice. No-op in loopback (no guests).
        }
    }
}
