using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Drives map votes. Gated on the current room being the map room — vanilla only
/// exposes voting through the map UI (unreachable mid-room); our headless drivers
/// must respect the same gate or votes would be accepted mid-combat.
///
/// Phantom policy (locked design decision): mirror the host player's vote.
/// Local autopilot: vote for the first child of the current coord (first row-0
/// point at run start).
/// </summary>
public static class MapDriver
{
    /// <summary>Votes we've enqueued but not yet seen land (action queue pauses during room transitions).</summary>
    private static readonly Dictionary<ulong, MapCoord> _inFlight = new();

    public static void Drive(Player player, RunState state)
    {
        MapSelectionSynchronizer sync = RunManager.Instance.MapSelectionSynchronizer;

        if (sync.GetVote(player) is { })
        {
            _inFlight.Remove(player.NetId);
            return; // vote landed
        }
        if (_inFlight.ContainsKey(player.NetId))
        {
            return; // vote enqueued, awaiting execution
        }

        // Gates mirror vanilla UI reachability. Phantoms: vote only once the host's
        // vote exists (implies his map is open — and implements mirror policy).
        // Local autopilot: map open with travel enabled. Headless votes are not
        // UI-gated by the game; an ungated vote mid-room would force travel.
        MapCoord? destination = LocalContext.IsMe(player)
            ? (NMapScreen.Instance is { IsOpen: true, IsTravelEnabled: true } ? PickFirstChild(state) : null)
            : MirrorHostVote(sync, state);
        if (destination == null)
        {
            return;
        }

        var vote = new MapVote { coord = destination.Value, mapGenerationCount = sync.MapGenerationCount };
        Observability.EventLog.Write("mod", LocalContext.IsMe(player) ? "autopilot_map_vote" : "phantom_map_vote",
            new { netId = player.NetId, coord = destination.Value.ToString() });
        _inFlight[player.NetId] = destination.Value;
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
            new VoteForMapCoordAction(player, state.MapLocation, vote));
    }

    private static MapCoord? MirrorHostVote(MapSelectionSynchronizer sync, RunState state)
    {
        Player? host = LocalContext.GetMe(state);
        return host == null ? null : sync.GetVote(host)?.coord;
    }

    private static MapCoord? PickFirstChild(RunState state)
    {
        if (state.CurrentMapCoord is MapCoord current)
        {
            return state.Map.GetPoint(current)?.Children.FirstOrDefault()?.coord;
        }
        return state.Map.GetPointsInRow(0).FirstOrDefault()?.coord;
    }
}
