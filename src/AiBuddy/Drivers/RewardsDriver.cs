using AiBuddy.Phantom;
using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Drives reward sets with ONE decision path for buddies and the local autopilot
/// (brain reuse is intentional — actuation differs, decisions never):
///   phantom → fabricated RewardSelectedMessage / RewardSetSkippedMessage (the exact
///             handlers a remote client's messages hit);
///   local   → SelectLocalReward / SkipLocalRewardsSet (the calls the UI makes);
///             a card pick surfaces as the reward screen — OverlayDrainDriver clicks
///             the brain's stashed choice (session.LocalCardPick).
/// Collection order: relic → gold → special card → potion (if it fits) → card → removal.
/// Select each reward once (a skipped card reward never becomes SuccessfullySelected —
/// track attempts, not success); close the set with the skip path when done.
/// LinkedRewardSet is never assumed decision-free (unused in v0.111.0 — revisit when used).
/// </summary>
public static class RewardsDriver
{
    /// <summary>Per-tick reward driving for one player. isLocalAutoplay: the local player under the debug autopilot.</summary>
    public static void Drive(Player player, RunState state, bool isLocalAutoplay)
    {
        if (PhantomSession.Current is not { } session)
        {
            return;
        }
        RewardsSetSynchronizer sync = RunManager.Instance.RewardsSetSynchronizer;
        RewardsSet? set;
        if (isLocalAutoplay)
        {
            set = session.LocalAutoplayRewardSet;
        }
        else
        {
            set = session.ActiveRewardSets.TryGetValue(player.NetId, out List<RewardsSet>? stack) && stack.Count > 0
                ? stack[^1] // selections apply to the top set only
                : null;
        }
        if (set == null)
        {
            return;
        }
        if (sync.IsRewardsSetCompleted(set))
        {
            CleanupSet(session, player.NetId, set, isLocalAutoplay);
            Observability.EventLog.Write("mod", isLocalAutoplay ? "autopilot_rewards_complete" : "phantom_rewards_complete",
                new { netId = player.NetId, setId = set.Id });
            return;
        }
        if (session.RewardSkipSent.Contains(set))
        {
            return; // close-out already sent; completion lands via the synchronizer
        }
        HashSet<Reward> attempted = session.RewardAttempts.GetValueOrDefault(set)
            ?? (session.RewardAttempts[set] = new HashSet<Reward>());

        Reward? next = set.Rewards
            .Where(r => !r.SuccessfullySelected && !attempted.Contains(r) && Collectible(r, player))
            .OrderBy(CollectionOrder)
            .FirstOrDefault();
        if (next == null)
        {
            if (isLocalAutoplay)
            {
                // Never close the set while a selection screen is open — the screen
                // orphans (nothing observes the backend) and strands the run.
                if (MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance?.Peek() is MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen)
                {
                    return;
                }
                Observability.EventLog.Write("mod", "autopilot_rewards_skip_rest", new { netId = player.NetId, setId = set.Id });
                sync.SkipLocalRewardsSet();
            }
            else
            {
                Observability.EventLog.Write("mod", "phantom_rewards_skip_rest", new { netId = player.NetId, setId = set.Id });
                sync.HandleRewardSetSkippedMessage(new RewardSetSkippedMessage
                {
                    location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation,
                    setId = set.Id,
                }, player.NetId);
            }
            session.RewardSkipSent.Add(set);
            return;
        }

        Observability.EventLog.Write("mod", isLocalAutoplay ? "autopilot_reward_select" : "phantom_reward_select",
            new { netId = player.NetId, setId = set.Id, reward = next.GetType().Name });
        attempted.Add(next);
        if (isLocalAutoplay)
        {
            if (next is CardReward cardReward)
            {
                // Brain pick-or-skip BEFORE opening the screen; the drain clicks the stash.
                int? pick = TrivialPolicy.PickCardReward(cardReward.Cards.Count(), player.Deck.Cards.Count);
                if (pick == null)
                {
                    Observability.EventLog.Write("mod", "autopilot_card_reward_skip", new { netId = player.NetId });
                    return; // leave unselected; close-out handles it
                }
                session.LocalCardPick = cardReward.Cards.ElementAt(pick.Value);
            }
            TaskHelper.RunSafely(sync.SelectLocalReward(next));
        }
        else
        {
            sync.HandleRewardSelectedMessage(new RewardSelectedMessage
            {
                location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation,
                setId = set.Id,
                rewardIndex = set.Rewards.IndexOf(next),
            }, player.NetId);
        }
    }

    private static void CleanupSet(PhantomSession session, ulong netId, RewardsSet set, bool isLocalAutoplay)
    {
        session.RewardAttempts.Remove(set);
        session.RewardSkipSent.Remove(set);
        if (isLocalAutoplay)
        {
            session.LocalAutoplayRewardSet = null;
        }
        else if (session.ActiveRewardSets.TryGetValue(netId, out List<RewardsSet>? stack))
        {
            stack.Remove(set);
            if (stack.Count == 0)
            {
                session.ActiveRewardSets.Remove(netId);
            }
        }
    }

    /// <summary>What the trivial driver collects blind vs leaves. LinkedRewardSet is never assumed decision-free.</summary>
    private static bool Collectible(Reward reward, Player player) => reward switch
    {
        RelicReward => true,
        GoldReward => true,
        SpecialCardReward => true,
        PotionReward => player.HasOpenPotionSlots, // full slots → leave it (brain-worthy later)
        CardReward => true,      // pick-or-skip is the brain's
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
