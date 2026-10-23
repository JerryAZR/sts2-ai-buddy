using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Testbench-only deterministic maps (AIBUDDY_TEST_MAP="monster,rest,treasure,…"):
/// rewrites the leftmost row-0→boss path's point types right after map generation,
/// so integration tests reliably reach the room type under test instead of hoping
/// for a favorable seed.
///
/// TESTBENCH ONLY: the map is seed-generated identically on every peer — rewriting
/// it would desync a real guest. Never enable outside host-only test runs.
/// </summary>
public static class TestMapDriver
{
    private static bool _applied;

    public static void Tick(RunState state)
    {
        if (_applied)
        {
            return;
        }
        string? spec = System.Environment.GetEnvironmentVariable("AIBUDDY_TEST_MAP");
        if (string.IsNullOrWhiteSpace(spec) || state.Map.GetRowCount() == 0)
        {
            return;
        }
        List<MapPoint> path = BuildLeftmostPath(state);
        if (path.Count == 0)
        {
            return;
        }
        // Note: CurrentMapCoord is already set to the start point on our first tick —
        // this must run before the first vote, which Neow guarantees (seconds away).
        List<MapPointType?> types = spec.Split(',').Select(s => ParseType(s.Trim())).ToList();
        int applied = 0;
        for (int i = 0; i < path.Count && i < types.Count; i++)
        {
            if (types[i] == null)
            {
                continue;
            }
            if (!path[i].CanBeModified)
            {
                Log.Warn($"[AiBuddy] TestMap: point {path[i].coord} is not modifiable — skipping.");
                continue;
            }
            path[i].PointType = types[i]!.Value;
            applied++;
        }
        _applied = true;
        Observability.EventLog.Write("test", "test_map_applied", new
        {
            spec,
            applied,
            path = path.Select(p => p.coord.ToString()).ToArray(),
        });
        Log.Info($"[AiBuddy] TestMap applied: {spec} ({applied} nodes on the leftmost path).");
    }

    /// <summary>Leftmost visitable path: from the start point's children through the boss. Spec index 0 = first room visited.</summary>
    private static List<MapPoint> BuildLeftmostPath(RunState state)
    {
        var path = new List<MapPoint>();
        MapPoint? point = state.Map.StartingMapPoint.Children.OrderBy(p => p.coord.col).FirstOrDefault();
        while (point != null)
        {
            path.Add(point);
            if (point == state.Map.BossMapPoint)
            {
                break;
            }
            point = point.Children.OrderBy(p => p.coord.col).FirstOrDefault();
        }
        return path;
    }

    private static MapPointType? ParseType(string name) => name.ToLowerInvariant() switch
    {
        "monster" or "combat" => MapPointType.Monster,
        "event" or "unknown" => MapPointType.Unknown,
        "rest" or "restsite" => MapPointType.RestSite,
        "treasure" => MapPointType.Treasure,
        "shop" => MapPointType.Shop,
        "elite" => MapPointType.Elite,
        "boss" => MapPointType.Boss,
        "ancient" => MapPointType.Ancient,
        _ => null,
    };
}
