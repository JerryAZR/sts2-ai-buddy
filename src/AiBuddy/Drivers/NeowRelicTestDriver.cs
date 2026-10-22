using AiBuddy.Phantom;
using AiBuddy.Policy;
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

    /// <summary>
    /// Every relic option Neow can offer (v0.111.0 audit). Expected outcomes are NOT
    /// duplicated here: a relic is expected to time out iff TrivialPolicy defers it
    /// (DeferredRewardRelics) — the policy and the test expectation share one source
    /// of truth, so the M3 flip can't desynchronize them.
    /// </summary>
    private static readonly string[] Cases =
    {
        // Direct grants (clean)
        "ArcaneScroll", "BoomingConch", "FishingRod", "GoldenPearl",
        "LavaRock", "NeowsTalisman", "NeowsTorment",
        "NeowsSacrifice", "NutritiousOyster", "PhialHolster", "Pomander",
        "StoneHumidifier", "WingedBoots",
        "CursedPearl", "LargeCapsule", "LeafyPoultice", "SilkenTress", "SilverCrucible",
        // Card-select prompts (choice fabrication)
        "LeadPaperweight", "MassiveScroll", "HeftyTablet", "ScrollBoxes",
        "NewLeaf", "PreciseScissors", "PrecariousShears",
        // Reward-set offers (deferred in TrivialPolicy until M3 RewardsSet fabrication lands)
        "LostCoffer", "Kaleidoscope", "NeowsBones", "SmallCapsule",
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
        // Wait until EVERY phantom's Neow event is done — test obtains must not
        // interleave with any phantom's live prompts (M1 invariant: ≥2 phantoms).
        EventSynchronizer events = RunManager.Instance.EventSynchronizer;
        if (events.Events.Any(e => !e.IsFinished && phantoms.Any(p => p.NetId == e.Owner.NetId)))
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
        foreach (string typeName in Cases)
        {
            RelicModel? canonical = ModelDb.AllRelics.FirstOrDefault(r => r.GetType().Name == typeName);
            if (canonical == null)
            {
                Log.Warn($"[AiBuddy] NeowRelicTest: relic type {typeName} not found in ModelDb.");
                fail++;
                continue;
            }
            bool expectComplete = !TrivialPolicy.DeferredRewardRelics.Contains(canonical.Id.Entry);

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

            // Expected-timeout cases must time out CLEANLY: a faulted obtain is a
            // crash, not the known reward-set gap, and must fail loudly.
            bool testPassed = expectComplete ? completed : (!completed && error == null);
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
