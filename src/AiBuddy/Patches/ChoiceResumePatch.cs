using AiBuddy.Phantom;
using HarmonyLib;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Patches;

/// <summary>
/// Phantom action resumption. Vanilla resumes a choice-paused action only when its
/// owner is the LOCAL player — a remote player's client sends
/// RequestResumeActionAfterPlayerChoiceMessage to the host. Phantoms have no client,
/// so without this any phantom card whose OnPlay opens a choice (Hologram,
/// NeowsFury, …) pauses forever (action stuck in GatheringPlayerChoice, queue never
/// empties, combat softlocks — observed).
///
/// We ARE the host: RequestResumeActionAfterPlayerChoice takes the host branch, which
/// resumes locally AND broadcasts ResumeActionAfterPlayerChoiceMessage to guests —
/// so this one seam covers the M7 guest path too.
/// </summary>
[HarmonyPatch(typeof(GameActionPlayerChoiceContext), nameof(GameActionPlayerChoiceContext.SignalPlayerChoiceEnded))]
internal static class ChoiceResumePatch
{
    private static void Prefix(GameActionPlayerChoiceContext __instance)
    {
        if (PhantomSession.Current is { } session && session.IsPhantom(__instance.Action.OwnerId))
        {
            Observability.EventLog.Write("mod", "phantom_action_resumed", new { action = __instance.Action.ToString() });
            RunManager.Instance.ActionQueueSynchronizer.RequestResumeActionAfterPlayerChoice(__instance.Action);
        }
    }
}
