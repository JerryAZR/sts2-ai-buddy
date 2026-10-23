using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace AiBuddy.Phantom;

/// <summary>One fabricated player. Identity is stable per save via <see cref="NetId"/>.</summary>
public sealed record PhantomDescriptor(ulong NetId, string DisplayName);

/// <summary>
/// All per-session phantom state: the registry AND every driver store (pending
/// choices, in-flight votes, proceeded events). Session boundaries swap
/// <see cref="Current"/> for a fresh instance, so cross-run state leakage is
/// impossible by construction — no reset method to forget to call or enlist in.
///
/// Harmony patches stay static and stateless; they read <see cref="Current"/>.
///
/// N-phantom invariant: nothing special-cases "the phantom" — all logic iterates
/// <see cref="Phantoms"/>. Integration tests run ≥2 phantoms from day one.
/// </summary>
public sealed class PhantomSession
{
    /// <summary>Default base of the phantom NetId range. Real SteamID64s are 17 digits — no collision.</summary>
    public const ulong DefaultBaseNetId = 1001;

    public static PhantomSession? Current { get; private set; }

    private readonly INetGameService? _netService; // null only in unit tests
    private readonly List<PhantomDescriptor> _phantoms = new();

    public IReadOnlyList<PhantomDescriptor> Phantoms => _phantoms;

    // Driver state stores. Owned here so their lifetime equals the session's.
    // Types live with their drivers; the session is just the container.
    public Dictionary<ulong, Drivers.ChoiceDriver.PendingPrompt> PendingChoices { get; } = new();
    public Dictionary<ulong, MapCoord> InFlightVotes { get; } = new();
    public HashSet<EventModel> ProceededEvents { get; } = new();
    /// <summary>
    /// Buddy turn sequence (design §3.5): index into <see cref="Phantoms"/> of the buddy currently
    /// taking its turn; -1 = no sequence running. The host triggers a sequence (the single door is
    /// CombatDriver.TriggerSequence); buddies then act strictly in registration order, one at a time.
    /// Per-combat state: cleared on every PlayPhase entry (turn boundary) and combat end.
    /// </summary>
    public int BuddySequenceIndex { get; set; } = -1;
    /// <summary>Phase of the current buddy in the sequence (see CombatDriver.BuddyPhase).</summary>
    public Drivers.CombatDriver.BuddyPhase BuddySequencePhase { get; set; }
    /// <summary>Plan-execution state for the buddy currently acting (null when Waking/Ending or no sequence).</summary>
    public Drivers.BuddyTurnExecution? CurrentBuddyTurn { get; set; }
    /// <summary>Phantom reward sets being driven (RewardsDriver), keyed by NetId; a STACK per player — the synchronizer applies selections to the top set regardless of setId, so we drive the top and pop on completion.</summary>
    public Dictionary<ulong, List<MegaCrit.Sts2.Core.Rewards.RewardsSet>> ActiveRewardSets { get; } = new();
    /// <summary>Rewards already selected in a set (a skipped card reward never becomes SuccessfullySelected — track attempts, not success). Keyed by set object: per-netId flags leak across sets.</summary>
    public Dictionary<MegaCrit.Sts2.Core.Rewards.RewardsSet, HashSet<MegaCrit.Sts2.Core.Rewards.Reward>> RewardAttempts { get; } = new();
    /// <summary>Sets for which the close-out skip message has been sent.</summary>
    public HashSet<MegaCrit.Sts2.Core.Rewards.RewardsSet> RewardSkipSent { get; } = new();
    /// <summary>Debug autopilot: the local player's offered reward set (driven by RewardsDriver like a buddy's).</summary>
    public MegaCrit.Sts2.Core.Rewards.RewardsSet? LocalAutoplayRewardSet { get; set; }
    /// <summary>The brain's card-reward pick for the local player — the screen drain clicks this card.</summary>
    public MegaCrit.Sts2.Core.Models.CardModel? LocalCardPick { get; set; }
    /// <summary>Rest-site option lists per player (live references into the synchronizer — removals are visible). Captured by RestSiteCapturePatch.</summary>
    public Dictionary<ulong, List<MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption>> RestSiteOptions { get; } = new();
    /// <summary>Rest-site option ids already attempted per player (failed options are never retried — termination guarantee).</summary>
    public Dictionary<ulong, HashSet<string>> RestSiteAttempts { get; } = new();
    /// <summary>Debug autopilot: local rest site finished, waiting for the drain to click the room's proceed button.</summary>
    public bool LocalRestSitePendingProceed { get; set; }
    /// <summary>Dead-buddy panics (woken by a race, force re-ended) logged once per buddy per session.</summary>
    public HashSet<ulong> DeadBuddyPanicLogged { get; } = new();

    private PhantomSession(INetGameService? netService) => _netService = netService;

    /// <summary>A new lobby/run becomes the current session, discarding all prior state.</summary>
    public static PhantomSession StartNew(INetGameService netService)
    {
        Current = new PhantomSession(netService);
        Observability.EventLog.Write("mod", "session_started");
        Log.Info("[AiBuddy] New phantom session started.");
        return Current;
    }

    /// <summary>Unit tests construct sessions without a game running.</summary>
    public static PhantomSession StartNewForTest() => new(null);

    public static void End()
    {
        if (Current != null)
        {
            Observability.EventLog.Write("mod", "session_ended", new { phantoms = Current._phantoms.Count });
            Current = null;
        }
    }

    /// <summary>Next free id in the phantom range (skips registered ids).</summary>
    public ulong PeekNextId()
    {
        ulong id = DefaultBaseNetId;
        while (IsRegistered(id))
        {
            id++;
        }
        return id;
    }

    /// <summary>Allocate and register the next phantom id in the range.</summary>
    public PhantomDescriptor RegisterPhantom(string displayName) => RegisterPhantom(displayName, PeekNextId());

    /// <summary>Register an explicit id (M6 resume path re-registers ids from the save).</summary>
    public PhantomDescriptor RegisterPhantom(string displayName, ulong netId)
    {
        if (IsRegistered(netId))
        {
            throw new InvalidOperationException($"Phantom NetId {netId} is already registered in this session.");
        }
        var descriptor = new PhantomDescriptor(netId, displayName);
        _phantoms.Add(descriptor);
        Observability.EventLog.Write("mod", "phantom_registered", new { netId, displayName });
        return descriptor;
    }

    public bool IsRegistered(ulong netId) => _phantoms.Any(p => p.NetId == netId);

    /// <summary>
    /// True iff this id is one of ours AND not a live connected peer (design hard
    /// rule 3: never fabricate for real clients — KitLib's NetId-1001 collision bug).
    /// </summary>
    public bool IsPhantom(ulong netId) => IsRegistered(netId) && !IsLivePeer(netId);

    private bool IsLivePeer(ulong netId) =>
        _netService is NetHostGameService host && host.ConnectedPeers.Any(p => p.peerId == netId);
}
