using AiBuddy.Phantom;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Debug-only consequence test (AIBUDDY_TEST_NEOW_RELICS=1): grants every Neow
/// relic option to the first phantom, sequentially, and asserts the CONSEQUENCES —
/// the obtain task completing (or timing out where we know support is missing),
/// not merely the choice. Self-asserting: results land in the event log as
/// test_relic_result / test_relics_done, with per-relic PASS/FAIL.
///
/// Loopback-only caveat: RelicCmd.Obtain is invoked directly, outside the action
/// queue — fine with no guests, would be a determinism violation on a live session.
/// </summary>
public static class NeowRelicTestDriver
{
    private const double TimeoutSeconds = 20;

    /// <summary>Every relic option Neow can offer (v0.111.0 audit). expectComplete=false marks the known reward-set gap (M3 flips them).</summary>
    private static readonly (string typeName, bool expectComplete)[] Cases =
    {
        // Direct grants (clean)
        ("ArcaneScroll", true), ("BoomingConch", true), ("FishingRod", true), ("GoldenPearl", true),
        ("LavaRock", true), ("NeowsTalisman", true), ("NeowsTorment", true),
        ("NeowsSacrifice", true), ("NutritiousOyster", true), ("PhialHolster", true), ("Pomander", true),
        ("StoneHumidifier", true), ("WingedBoots", true),
        ("CursedPearl", true), ("LargeCapsule", true), ("LeafyPoultice", true), ("SilkenTress", true), ("SilverCrucible", true),
        // Card-select prompts (choice fabrication)
        ("LeadPaperweight", true), ("MassiveScroll", true), ("HeftyTablet", true), ("ScrollBoxes", true),
        ("NewLeaf", true), ("PreciseScissors", true), ("PrecariousShears", true),
        // Reward-set offers — KNOWN GAP until M3 RewardsSet fabrication lands (empirically verified: all four hang)
        ("LostCoffer", false), ("Kaleidoscope", false), ("NeowsBones", false), ("SmallCapsule", false),
    };

    private static bool _started;

    public static void Tick(RunState state)
    {
        if (_started || System.Environment.GetEnvironmentVariable("AIBUDDY_TEST_NEOW_RELICS") != "1")
        {
            return;
        }
        if (PhantomSession.Current?.Phantoms is not { Count: > 0 } phantoms)
        {
            return;
        }
        // Wait until the phantom's Neow event is done so its prompts don't interleave with ours.
        EventSynchronizer events = RunManager.Instance.EventSynchronizer;
        if (events.Events.Any(e => e.Owner.NetId == phantoms[0].NetId && !e.IsFinished))
        {
            return;
        }
        _started = true;
        TaskHelper.RunSafely(Run(state, phantoms[0].NetId));
    }

    private static async Task Run(RunState state, ulong netId)
    {
        Player? player = state.GetPlayer(netId);
        if (player == null)
        {
            Log.Warn($"[AiBuddy] NeowRelicTest: phantom {netId} not found.");
            return;
        }

        int pass = 0, fail = 0;
        foreach ((string typeName, bool expectComplete) in Cases)
        {
            RelicModel? canonical = ModelDb.AllRelics.FirstOrDefault(r => r.GetType().Name == typeName);
            if (canonical == null)
            {
                Log.Warn($"[AiBuddy] NeowRelicTest: relic type {typeName} not found in ModelDb.");
                fail++;
                continue;
            }

            int deckBefore = player.Deck.Cards.Count;
            string? error = null;
            bool completed;
            try
            {
                RelicModel relic = canonical.ToMutable();
                relic.Owner = player;
                Task<RelicModel> obtain = RelicCmd.Obtain(relic, player);
                Task done = await Task.WhenAny(obtain, Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds)));
                completed = done == obtain;
                if (completed && obtain.IsFaulted)
                {
                    error = obtain.Exception?.InnerException?.Message ?? "faulted";
                    completed = false;
                }
            }
            catch (Exception ex)
            {
                completed = false;
                error = ex.Message;
            }

            bool testPassed = completed == expectComplete;
            if (testPassed) pass++; else fail++;
            Observability.EventLog.Write("test", "test_relic_result", new
            {
                relic = typeName,
                completed,
                expectedComplete = expectComplete,
                deckDelta = player.Deck.Cards.Count - deckBefore,
                error,
                verdict = testPassed ? "PASS" : "FAIL",
            });
            Log.Info($"[AiBuddy] NeowRelicTest {typeName}: {(testPassed ? "PASS" : "FAIL")} (completed={completed}, expected={expectComplete}{(error != null ? $", error={error}" : "")})");
        }

        Observability.EventLog.Write("test", "test_relics_done", new { pass, fail });
        Log.Info($"[AiBuddy] NeowRelicTest finished: {pass} pass, {fail} fail of {Cases.Length}.");
    }
}
