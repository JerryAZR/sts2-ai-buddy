using AiBuddy.Phantom;
using HarmonyLib;
using MegaCrit.Sts2.Core.Rewards;

namespace AiBuddy.Patches;

/// <summary>
/// Captures phantom reward sets as they're offered. Postfix on an async method runs
/// at the first suspension — the set reference is captured early and its Rewards
/// list fills in by the time RewardsDriver consumes it (same object).
/// </summary>
[HarmonyPatch(typeof(RewardsSet), nameof(RewardsSet.Offer))]
internal static class RewardsSetOfferPatch
{
    private static readonly bool AutoplayLocal =
        System.Environment.GetEnvironmentVariable("AIBUDDY_AUTOPLAY_LOCAL") == "1";

    private static void Postfix(RewardsSet __instance)
    {
        if (PhantomSession.Current is not { } session)
        {
            return;
        }
        if (session.IsPhantom(__instance.Player.NetId))
        {
            // Stack per player: the synchronizer's rewardsStack applies selections to
            // the TOP set (setId is bookkeeping only), so we drive top-first.
            if (!session.ActiveRewardSets.TryGetValue(__instance.Player.NetId, out var stack))
            {
                stack = session.ActiveRewardSets[__instance.Player.NetId] = new List<RewardsSet>();
            }
            stack.Add(__instance);
            Observability.EventLog.Write("mod", "phantom_rewards_offered", new { netId = __instance.Player.NetId, setId = __instance.Id, depth = stack.Count });
        }
        else if (AutoplayLocal && MegaCrit.Sts2.Core.Context.LocalContext.IsMe(__instance.Player))
        {
            // Debug autopilot: the host's own reward screen is drained by AutomationTick.
            session.LocalAutoplayRewardSet = __instance;
        }
    }
}
