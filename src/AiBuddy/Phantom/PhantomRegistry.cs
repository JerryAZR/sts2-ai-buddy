using MegaCrit.Sts2.Core.Logging;

namespace AiBuddy.Phantom;

/// <summary>One fabricated player. Identity is stable per save via <see cref="NetId"/>.</summary>
public sealed record PhantomDescriptor(ulong NetId, string DisplayName);

/// <summary>
/// Session registry of all phantoms. N-phantom invariant: nothing in the mod
/// may special-case "the phantom" — every subsystem iterates this collection.
/// Integration tests run with ≥2 phantoms from day one.
/// </summary>
public static class PhantomRegistry
{
    /// <summary>Default base of the phantom NetId range. Real SteamID64s are 17 digits — no collision.</summary>
    public const ulong DefaultBaseNetId = 1001;

    private static readonly List<PhantomDescriptor> _phantoms = new();

    public static IReadOnlyList<PhantomDescriptor> All => _phantoms;

    /// <summary>Next id in the range. Lobby-scoped lifetime; resume re-registers saved ids explicitly.</summary>
    public static ulong AllocateNextId() => DefaultBaseNetId + (ulong)_phantoms.Count;

    public static void Register(ulong netId, string displayName)
    {
        if (IsRegistered(netId))
        {
            Log.Warn($"[AiBuddy] Phantom {netId} already registered; ignoring.");
            return;
        }
        _phantoms.Add(new PhantomDescriptor(netId, displayName));
        Observability.EventLog.Write("mod", "phantom_registered", new { netId, displayName });
    }

    public static bool IsRegistered(ulong netId) => _phantoms.Any(p => p.NetId == netId);

    /// <summary>
    /// True iff this id is one of ours. TODO(M3/M7): per design hard rule 3, this must ALSO
    /// require "not in the transport's ConnectedPeers" before any real client can join,
    /// so a live peer can never be mistaken for a phantom (KitLib's NetId-1001 bug).
    /// </summary>
    public static bool IsPhantom(ulong netId) => IsRegistered(netId);

    /// <summary>Lobby torn down / run ended — forget all phantoms.</summary>
    public static void Clear()
    {
        if (_phantoms.Count > 0)
        {
            Observability.EventLog.Write("mod", "phantoms_cleared", new { count = _phantoms.Count });
            _phantoms.Clear();
        }
    }
}
