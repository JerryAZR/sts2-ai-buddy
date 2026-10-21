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
    /// <summary>NetId → combat turn number for which the host armed the phantom to act (the "Buddy: act" button).</summary>
    public Dictionary<ulong, int> ArmedPhantoms { get; } = new();

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
