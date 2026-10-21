using AiBuddy.Phantom;
using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Drives map votes. Gates mirror vanilla UI reachability: phantoms vote only once
/// the host's vote exists (implies his map is open — and implements mirror policy);
/// the local autopilot votes with the map open and travel enabled. Headless votes
/// are not UI-gated by the game; an ungated vote mid-room would force travel.
/// </summary>
public static class MapDriver
{
    public static void Drive(Player player, RunState state)
    {
        if (PhantomSession.Current is not { } session)
        {
            return;
        }
        MapSelectionSynchronizer sync = RunManager.Instance.MapSelectionSynchronizer;

        if (sync.GetVote(player) != null)
        {
            session.InFlightVotes.Remove(player.NetId);
            return; // vote landed
        }
        if (session.InFlightVotes.ContainsKey(player.NetId))
        {
            return; // vote enqueued, awaiting execution
        }

        Player? host = LocalContext.GetMe(state);
        MapCoord? hostVote = host == null ? null : sync.GetVote(host)?.coord;
        MapCoord? destination = TrivialPolicy.PickMapDestination(state, player, hostVote);
        if (destination == null)
        {
            return;
        }
        if (LocalContext.IsMe(player) && NMapScreen.Instance is not { IsOpen: true, IsTravelEnabled: true })
        {
            return; // local player votes only with the map actually open (vanilla reachability)
        }

        // Brain boundary: the destination must be reachable from the current location.
        if (!IsReachable(state, destination.Value))
        {
            Log.Warn($"[AiBuddy] Policy picked unreachable map coord {destination} from {state.CurrentMapCoord}; refusing.");
            Observability.EventLog.Write("mod", "policy_output_rejected", new { coord = destination.Value.ToString() });
            return;
        }

        var vote = new MapVote { coord = destination.Value, mapGenerationCount = sync.MapGenerationCount };
        Observability.EventLog.Write("mod", LocalContext.IsMe(player) ? "autopilot_map_vote" : "phantom_map_vote",
            new { netId = player.NetId, coord = destination.Value.ToString() });
        session.InFlightVotes[player.NetId] = destination.Value;
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
            new VoteForMapCoordAction(player, state.MapLocation, vote));
    }

    private static bool IsReachable(RunState state, MapCoord destination)
    {
        if (state.CurrentMapCoord is not { } current)
        {
            return state.Map.GetPointsInRow(0).Any(p => p.coord == destination);
        }
        return state.Map.GetPoint(current)?.Children.Any(c => c.coord == destination) == true;
    }
}
