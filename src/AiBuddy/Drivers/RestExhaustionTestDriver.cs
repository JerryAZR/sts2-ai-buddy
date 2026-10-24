using AiBuddy.Phantom;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// TESTBENCH ONLY (AIBUDDY_TEST_REST_EXHAUSTION=1): fully upgrades every phantom's
/// deck at run start so Smith fails at rest sites ("no upgradable cards"), forcing
/// the brain's Heal-fallback path. Pins the claim that a phantom rest site always
/// completes even when the preferred option fails. Never enabled in shipped code.
/// </summary>
public static class RestExhaustionTestDriver
{
    private static bool _applied;

    public static void Tick(RunState state)
    {
        if (_applied || System.Environment.GetEnvironmentVariable("AIBUDDY_TEST_REST_EXHAUSTION") != "1"
            || PhantomSession.Current is not { } session)
        {
            return;
        }
        _applied = true;
        foreach (PhantomDescriptor phantom in session.Phantoms)
        {
            var player = state.GetPlayer(phantom.NetId);
            if (player == null)
            {
                continue;
            }
            int upgraded = 0;
            foreach (var card in player.Deck.Cards)
            {
                if (card.IsUpgradable)
                {
                    card.UpgradeInternal();
                    upgraded++;
                }
            }
            Observability.EventLog.Write("mod", "test_rest_exhaustion_applied", new { netId = phantom.NetId, upgraded });
            Log.Info($"[AiBuddy] TEST: fully upgraded {upgraded} deck cards for phantom {phantom.NetId} (rest-exhaustion repro).");
        }
    }
}
