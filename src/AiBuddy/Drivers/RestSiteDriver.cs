using System.Reflection;
using AiBuddy.Phantom;
using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Drives rest-site choices. Phantoms fabricate OptionIndexChosenMessage into the
/// RestSiteSynchronizer's remote-message handler (the exact seam a real client's
/// message hits); the local autopilot uses ChooseLocalOption (the UI's own call).
/// Brain rule: heal below half HP, else a random other option. A failed option
/// (OnSelect returned false) is never retried — guaranteed termination.
/// </summary>
public static class RestSiteDriver
{
    private static readonly MethodInfo HandleOptionChosen =
        typeof(RestSiteSynchronizer).GetMethod("HandleRestSiteOptionChosenMessage", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException("RestSiteSynchronizer.HandleRestSiteOptionChosenMessage");

    /// <summary>Per-tick rest-site driving for one player (phantom or local autopilot).</summary>
    public static void Drive(Player player, RunState state, bool isLocalAutoplay)
    {
        if (PhantomSession.Current is not { } session
            || !session.RestSiteOptions.TryGetValue(player.NetId, out List<RestSiteOption>? options))
        {
            return;
        }
        if (options.Count == 0)
        {
            // Completion: ChooseOption empties the list (chosen options are removed;
            // hooks may clear the rest) and the synchronizer completes the site.
            session.RestSiteOptions.Remove(player.NetId);
            session.RestSiteAttempts.Remove(player.NetId);
            Observability.EventLog.Write("mod", "rest_site_done", new { netId = player.NetId });
            if (isLocalAutoplay)
            {
                // Room exit is a local-UI step (NRestSiteRoom.ProceedButton — rooms are
                // not on the overlay stack). The drain picks it up from this flag.
                session.LocalRestSitePendingProceed = true;
            }
            return;
        }

        HashSet<string> attempted = session.RestSiteAttempts.GetValueOrDefault(player.NetId)
            ?? (session.RestSiteAttempts[player.NetId] = new HashSet<string>());
        var available = options.Select((o, i) => (option: o, index: i))
            .Where(x => !attempted.Contains(x.option.OptionId))
            .ToList();
        if (available.Count == 0)
        {
            return; // everything left already failed once — wait for the room to resolve
        }

        var view = available.Select(x => new RestSiteOptionView(x.index, x.option.OptionId, x.option is MendRestSiteOption)).ToList();
        int? picked = TrivialPolicy.PickRestSiteOption(player.Creature.CurrentHp, player.Creature.MaxHp, view);
        if (picked is not int index)
        {
            return;
        }
        string optionId = options[index].OptionId;
        attempted.Add(optionId); // never retry a failed option
        Observability.EventLog.Write("mod", isLocalAutoplay ? "autopilot_rest_choice" : "phantom_rest_choice",
            new { netId = player.NetId, optionId, index });

        if (isLocalAutoplay)
        {
            TaskHelper.RunSafely(RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(index));
        }
        else
        {
            HandleOptionChosen.Invoke(RunManager.Instance.RestSiteSynchronizer, new object[]
            {
                new OptionIndexChosenMessage
                {
                    type = OptionIndexType.RestSite,
                    optionIndex = (uint)index,
                    location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation,
                },
                player.NetId,
            });
            // TODO(M7): the fabricated choice must also reach guests — check whether the
            // handler broadcasts or whether we need a forged-sender send.
        }
    }
}
