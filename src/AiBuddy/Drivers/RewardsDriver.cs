using AiBuddy.Phantom;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Drives phantom reward sets (design: collection order relic → gold → blind
/// composites → potion if it fits → card reward via brain → card removal via brain).
///
/// Fabrication seam: RewardsSetSynchronizer.HandleRewardSelectedMessage / _Skipped —
/// the exact handlers a remote client's messages trigger. Sets are captured by
/// RewardsSetOfferPatch; one selection per tick per phantom; the set is closed with
/// a skip message once everything we collect is done (covers uncollected potions,
/// LinkedRewardSets — unknown/unused in v0.111.0, never assumed decision-free).
/// </summary>
public static class RewardsDriver
{
    /// <summary>Per-tick reward driving for one phantom (host only).</summary>
    public static void Drive(Player player, RunState state)
    {
        if (PhantomSession.Current is not { } session
            || !session.ActiveRewardSets.TryGetValue(player.NetId, out List<RewardsSet>? stack)
            || stack.Count == 0)
        {
            return;
        }
        RewardsSet set = stack[^1]; // selections apply to the top set only
        RewardsSetSynchronizer sync = RunManager.Instance.RewardsSetSynchronizer;
        if (sync.IsRewardsSetCompleted(set))
        {
            stack.RemoveAt(stack.Count - 1);
            session.RewardAttempts.Remove(set);
            session.RewardSkipSent.Remove(set);
            if (stack.Count == 0)
            {
                session.ActiveRewardSets.Remove(player.NetId);
            }
            Observability.EventLog.Write("mod", "phantom_rewards_complete", new { netId = player.NetId, setId = set.Id });
            return;
        }
        if (session.RewardSkipSent.Contains(set))
        {
            return; // set close-out already sent; completion lands via the synchronizer
        }
        HashSet<Reward> attempted = session.RewardAttempts.GetValueOrDefault(set) ?? (session.RewardAttempts[set] = new HashSet<Reward>());

        // Select each collectible reward exactly once (one per tick). Note: a SKIPPED
        // card reward resolves with SuccessfullySelected == false by game design — the
        // set closes via the skip message, not via per-reward success.
        Reward? next = set.Rewards
            .Where(r => !r.SuccessfullySelected && !attempted.Contains(r) && Collectible(r, player))
            .OrderBy(CollectionOrder)
            .FirstOrDefault();
        if (next == null)
        {
            // Everything collected or deliberately left (skipped card rewards, full
            // potion slots, linked sets) → close the set.
            Observability.EventLog.Write("mod", "phantom_rewards_skip_rest", new { netId = player.NetId, setId = set.Id });
            sync.HandleRewardSetSkippedMessage(new RewardSetSkippedMessage
            {
                location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation,
                setId = set.Id,
            }, player.NetId);
            session.RewardSkipSent.Add(set);
            return;
        }

        Observability.EventLog.Write("mod", "phantom_reward_select",
            new { netId = player.NetId, setId = set.Id, reward = next.GetType().Name });
        attempted.Add(next);
        sync.HandleRewardSelectedMessage(new RewardSelectedMessage
        {
            location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation,
            setId = set.Id,
            rewardIndex = set.Rewards.IndexOf(next),
        }, player.NetId);
    }

    /// <summary>What the trivial driver collects blind vs leaves. LinkedRewardSet is never assumed decision-free (unused in v0.111.0 — revisit when the game uses it).</summary>
    private static bool Collectible(Reward reward, Player player) => reward switch
    {
        RelicReward => true,
        GoldReward => true,
        SpecialCardReward => true,
        PotionReward => player.HasOpenPotionSlots, // full slots → leave it (brain-worthy later)
        CardReward => true,      // pick-or-skip is the brain's, inside the reward's own prompt
        CardRemovalReward => true,
        LinkedRewardSet => false,
        _ => false,
    };

    private static int CollectionOrder(Reward reward) => reward switch
    {
        RelicReward => 0,
        GoldReward => 1,
        SpecialCardReward => 2,
        PotionReward => 3,
        CardReward => 4,
        CardRemovalReward => 5,
        _ => 9,
    };
}
