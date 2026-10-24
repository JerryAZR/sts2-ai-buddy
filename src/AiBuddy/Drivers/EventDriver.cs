using System.Reflection;
using AiBuddy.Phantom;
using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
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

    private static readonly MethodInfo PlayerVotedForSharedOption =
        typeof(EventSynchronizer).GetMethod("PlayerVotedForSharedOptionIndex", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException("EventSynchronizer.PlayerVotedForSharedOptionIndex");

    private static readonly FieldInfo PageIndexField =
        typeof(EventSynchronizer).GetField("_pageIndex", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException("EventSynchronizer._pageIndex");

    private static readonly FieldInfo PlayerVotesField =
        typeof(EventSynchronizer).GetField("_playerVotes", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException("EventSynchronizer._playerVotes");

    /// <summary>Drive the given player's current event one step, if there is a stable page with a valid option.</summary>
    public static void Drive(Player player, RunState state)
    {
        EventSynchronizer sync = RunManager.Instance.EventSynchronizer;
        if (sync.Events.Count == 0)
        {
            return;
        }
        EventModel? ev = sync.Events.FirstOrDefault(e => e.Owner == player);
        if (ev == null)
        {
            return;
        }
        bool isPhantom = !LocalContext.IsMe(player);
        if (isPhantom)
        {
            PhantomSession.Current?.MarkFlowActive(player.NetId, PhantomSession.RoomFlow.Event);
        }
        if (ev.IsFinished)
        {
            // Event page flow mechanically finished: the brain answered every page.
            if (isPhantom)
            {
                PhantomSession.Current?.MarkFlowBrainDone(player.NetId, PhantomSession.RoomFlow.Event);
            }
            // Proceed is a local-UI concept (opens the map); phantoms have no screen,
            // so only the autopilot needs it. Guard on the CURRENT room being this
            // event's room: finished events linger in the synchronizer after exit,
            // and re-proceeding from a stale event force-opens the map mid-room.
            if (LocalContext.IsMe(player)
                && state.CurrentRoom is EventRoom
                && PhantomSession.Current != null
                && PhantomSession.Current.ProceededEvents.Add(ev))
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
        // Build the detached view (control layer evaluates WillKill delegates host-side);
        // the brain sees plain data only.
        var view = new EventView(ev.Id.Entry, ev.CurrentOptions.Select((o, i) =>
            new EventOptionView(i, o.IsLocked, o.WillKillPlayer?.Invoke(ev.Owner) == true, o.Relic?.Id.Entry)).ToList());
        if (TrivialPolicy.PickEventOption(view) is not int index)
        {
            return;
        }
        // Brain boundary: re-validate the policy's choice before actuating it.
        if (index < 0 || index >= ev.CurrentOptions.Count || ev.CurrentOptions[index].IsLocked)
        {
            Log.Warn($"[AiBuddy] Policy returned invalid event option index {index} for {ev.Id} ({ev.CurrentOptions.Count} options); refusing to actuate.");
            Observability.EventLog.Write("mod", "policy_output_rejected", new { eventId = ev.Id.Entry, index });
            return;
        }

        bool isLocal = LocalContext.IsMe(player);

        if (sync.IsShared && !isLocal)
        {
            // Shared events: the party votes per page; the host tallies and RNG-picks
            // among votes, then broadcasts the chosen option (guests need nothing but
            // the vote — execution flows from the host's SharedEventOptionChosenMessage).
            uint page = (uint)PageIndexField.GetValue(sync)!;
            var votes = (List<uint?>)PlayerVotesField.GetValue(sync)!;
            int slot = state.GetPlayerSlotIndex(player);
            if (slot < votes.Count && votes[slot].HasValue)
            {
                return; // already voted this page
            }
            Observability.EventLog.Write("mod", "phantom_shared_event_vote",
                new { netId = player.NetId, eventId = ev.Id.Entry, index, page });
            PlayerVotedForSharedOption.Invoke(sync, new object[] { player, (uint)index, page });
            PhantomNet.BroadcastAsPhantom(player.NetId, new MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync.VotedForSharedEventOptionMessage
            {
                optionIndex = (uint)index,
                pageIndex = page,
                location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation,
            });
            return;
        }

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
            // M7: guests re-execute the phantom's event choice from the forged wire message.
            PhantomNet.BroadcastAsPhantom(player.NetId, new MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync.OptionIndexChosenMessage
            {
                type = MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync.OptionIndexType.Event,
                optionIndex = (uint)index,
                location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation,
            });
        }
    }
}
