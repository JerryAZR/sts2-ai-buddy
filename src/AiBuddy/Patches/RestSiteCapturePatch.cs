using System.Collections;
using System.Reflection;
using AiBuddy.Phantom;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Patches;

/// <summary>
/// Captures each phantom's rest-site options when a rest site begins. The options
/// list reference is shared with the synchronizer — removals by ChooseOption are
/// visible through it, which is how RestSiteDriver observes completion.
/// </summary>
[HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.BeginRestSite))]
internal static class RestSiteCapturePatch
{
    private static readonly bool AutoplayLocal =
        System.Environment.GetEnvironmentVariable("AIBUDDY_AUTOPLAY_LOCAL") == "1";

    // PlayerRestSite is a private nested class; its "options" field holds List<RestSiteOption>.
    private static readonly FieldInfo RestSitesField =
        typeof(RestSiteSynchronizer).GetField("_restSites", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException("RestSiteSynchronizer._restSites");

    private static readonly FieldInfo OptionsField =
        (RestSitesField.FieldType.IsGenericType ? RestSitesField.FieldType.GetGenericArguments()[0] : null)
            ?.GetField("options")
        ?? throw new MissingFieldException("PlayerRestSite.options");

    private static void Postfix(RestSiteSynchronizer __instance)
    {
        if (PhantomSession.Current is not { } session)
        {
            return;
        }
        RunState? state = RunManager.Instance?.DebugOnlyGetState();
        if (state == null)
        {
            return;
        }
        var restSites = (IList)RestSitesField.GetValue(__instance)!;
        for (int i = 0; i < state.Players.Count && i < restSites.Count; i++)
        {
            Player player = state.Players[i];
            bool capture = session.IsPhantom(player.NetId)
                || (AutoplayLocal && MegaCrit.Sts2.Core.Context.LocalContext.IsMe(player));
            if (!capture)
            {
                continue;
            }
            var options = (List<RestSiteOption>)OptionsField.GetValue(restSites[i])!;
            session.RestSiteOptions[player.NetId] = options;
            Observability.EventLog.Write("mod", "rest_site_began",
                new { netId = player.NetId, options = options.Select(o => o.OptionId).ToArray() });
        }
    }
}
