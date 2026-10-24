using System.Reflection;
using AiBuddy.Phantom;
using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Drives treasure rooms. Two per-player flows:
/// 1. Chest (decision-free freebies): humans click the chest; phantoms run the same
///    OneOffSynchronizer.DoTreasureRoomRewards the local click would reach, plus a
///    forged TreasureChestOpenedMessage so guests apply the gold to the PHANTOM
///    (the message handler keys everything by senderId; the vanilla local path
///    would stamp the host).
/// 2. Relic vote: every peer blocks until all votes are in (wait-all IS the pacing
///    gate, so no vote-gate integration). Policy: random index — the same policy
///    vanilla's own fake-multiplayer path uses (TreasureRoomRelicSynchronizer).
/// Phantoms act through PickRelicAction on the action queue (OwnerId carries the
/// phantom id through the normal broadcast — no forge needed); the local autopilot
/// uses PickRelicLocally like the UI does.
/// </summary>
public static class TreasureDriver
{
    private static readonly MethodInfo DoTreasureRoomRewards =
        typeof(OneOffSynchronizer).GetMethod("DoTreasureRoomRewards", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException("OneOffSynchronizer.DoTreasureRoomRewards");

    public static void Drive(Player player, RunState state)
    {
        if (state.CurrentRoom is not TreasureRoom room || PhantomSession.Current is not { } session)
        {
            return;
        }
        bool isLocal = LocalContext.IsMe(player);
        TreasureRoomRelicSynchronizer sync = RunManager.Instance.TreasureRoomRelicSynchronizer;

        if (!isLocal && session.TreasureChestOpened.Add(player.NetId))
        {
            Observability.EventLog.Write("mod", "phantom_treasure_chest", new { netId = player.NetId });
            TaskHelper.RunSafely((System.Threading.Tasks.Task<int>)DoTreasureRoomRewards.Invoke(
                RunManager.Instance.OneOffSynchronizer, new object[] { player })!);
            PhantomNet.BroadcastAsPhantom(player.NetId, new TreasureChestOpenedMessage
            {
                Location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation,
            });
        }
        // Local chest: the drain runs NTreasureRoom.OpenChest (the UI path — the relic
        // display and the proceed button's Skip->Proceed switch hang off it) and marks
        // TreasureChestOpened; the relic pick below then unlocks.

        if (sync.CurrentRelics is { Count: > 0 } relics
            && session.TreasureChestOpened.Contains(player.NetId) // local: after the UI chest flow
            && !sync.GetPlayerVote(player).voteReceived
            && !session.TreasurePickSent.ContainsKey(player.NetId))
        {
            // Dupes trigger a relic fight; humans avoid them by watching live votes.
            // Taken = landed votes ∪ our own in-flight picks this room.
            var taken = new HashSet<int>(session.TreasurePickSent.Values);
            foreach (Player other in state.Players)
            {
                if (other.NetId != player.NetId && sync.GetPlayerVote(other) is { voteReceived: true, index: { } takenIdx })
                {
                    taken.Add(takenIdx);
                }
            }
            int index = TrivialPolicy.PickTreasureRelic(relics.Count, taken);
            session.TreasurePickSent[player.NetId] = index;
            Observability.EventLog.Write("mod", isLocal ? "autopilot_treasure_pick" : "phantom_treasure_pick",
                new { netId = player.NetId, index, relic = relics[index].Id.Entry });
            if (isLocal)
            {
                sync.PickRelicLocally(index);
            }
            else
            {
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PickRelicAction(player, index));
            }
        }

        if (isLocal && session.TreasureChestOpened.Contains(player.NetId)
            && session.TreasurePickSent.ContainsKey(player.NetId))
        {
            // Local done: room exit is a local-UI step (NTreasureRoom.ProceedButton).
            // (Empty chests can't be detected here — CurrentRelics is nulled
            // synchronously at EndRelicVoting — but MP chests always offer relics.)
            session.LocalTreasurePendingProceed = true;
        }    }
}
