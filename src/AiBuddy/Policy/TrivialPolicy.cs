using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Policy;

/// <summary>
/// v1 trivial policy: pure decision functions over game state. Shared by phantom
/// drivers and the debug local-player autopilot. This is the seam where a real
/// brain (heuristic or LLM) plugs in later — decisions only, never actuation.
/// Drivers re-validate every returned choice before actuating it (the brain is
/// untrusted, per design §3.5).
/// </summary>
public static class TrivialPolicy
{
    /// <summary>
    /// Relics whose obtain-flow opens a reward-set screen we can't fabricate yet (M2 subset carve-out).
    /// Exhaustive for Neow as of v0.111.0: all 24 relic options audited; only these two call
    /// RewardsCmd.OfferCustom from AfterObtained.
    /// </summary>
    private static readonly HashSet<string> DeferredRewardRelics = new() { "LOST_COFFER", "KALEIDOSCOPE" };

    /// <summary>
    /// First valid option. Valid = not locked, won't kill the player, not deferred.
    /// Returns null when no valid option exists (caller logs + waits for a better policy).
    /// </summary>
    public static int? PickEventOption(EventModel ev)
    {
        IReadOnlyList<EventOption> options = ev.CurrentOptions;
        for (int i = 0; i < options.Count; i++)
        {
            EventOption o = options[i];
            if (o.IsLocked || o.WillKillPlayer?.Invoke(ev.Owner) == true)
            {
                continue;
            }
            if (o.Relic != null && DeferredRewardRelics.Contains(o.Relic.Id.Entry))
            {
                continue;
            }
            return i;
        }
        Log.Warn($"[AiBuddy] No valid option in event {ev.Id} for player {ev.Owner.NetId}.");
        return null;
    }

    /// <summary>Index-choice prompts (choose-a-card, bundles): always take the first. Never skips in v1.</summary>
    public static int PickIndex(int optionCount) => 0;

    /// <summary>
    /// Multi-select prompts: the first MinSelect options (or none when cancelable
    /// with no minimum). Returns null when the constraints are unsatisfiable —
    /// fabricating a short selection would send a result the game considers invalid,
    /// so the driver must skip fabrication and log instead.
    /// </summary>
    public static List<int>? PickIndexes(int optionCount, int minSelect, int maxSelect, bool cancelable)
    {
        if (optionCount < minSelect)
        {
            return null; // unsatisfiable — refuse rather than emit an invalid selection
        }
        if (cancelable && minSelect == 0)
        {
            return new List<int>();
        }
        int count = Math.Clamp(Math.Max(minSelect, 1), 0, Math.Min(maxSelect, optionCount));
        return Enumerable.Range(0, count).ToList();
    }

    /// <summary>
    /// Map votes. Locked design decision: phantoms mirror the host player's vote
    /// (abstention risks stalling the lobby). Local autopilot takes the first child
    /// of the current coord (first row-0 point at run start).
    /// </summary>
    public static MapCoord? PickMapDestination(RunState state, Player player, MapCoord? hostVote)
    {
        if (!LocalContext.IsMe(player))
        {
            return hostVote;
        }
        if (state.CurrentMapCoord is MapCoord current)
        {
            return state.Map.GetPoint(current)?.Children.FirstOrDefault()?.coord;
        }
        return state.Map.GetPointsInRow(0).FirstOrDefault()?.coord;
    }
}
