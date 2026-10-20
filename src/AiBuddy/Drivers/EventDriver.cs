using AiBuddy.Policy;
using System.Reflection;

using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
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

    /// <summary>Drive the given player's current event one step, if there is a stable page with a valid option.</summary>
    public static void Drive(Player player)
    {
        EventSynchronizer sync = RunManager.Instance.EventSynchronizer;
        if (sync.Events.Count == 0 || sync.IsShared)
        {
            // Shared/collaboration events are M4 (vote mirroring).
            return;
        }
        EventModel? ev = sync.Events.FirstOrDefault(e => e.Owner == player);
        if (ev == null || ev.IsFinished)
        {
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
