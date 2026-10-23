using System.Reflection;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Phantom;

/// <summary>
/// Outbound forged-sender broadcasts (M7): everything a phantom "says" must reach
/// guests with the PHANTOM as wire sender, or their sims wait forever. The wire
/// format carries a sender id and the host relay preserves it (overrideSenderId) —
/// so we send to each connected peer via the host's own internal send with the
/// phantom's id stamped. No-op when there are no live peers (loopback tests).
///
/// Complements PhantomSendGuardPatch: the guard blocks sends TO phantom ids (no
/// transport connection); this sends FROM phantom ids TO real peers.
/// </summary>
public static class PhantomNet
{
    private static readonly MethodInfo SendToClientInternal =
        typeof(NetHostGameService).GetMethod("SendMessageToClientInternal", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException("NetHostGameService.SendMessageToClientInternal");

    private static readonly MethodInfo ToChannelId =
        typeof(NetTransferModeExtensions).GetMethod(nameof(NetTransferModeExtensions.ToChannelId))
        ?? throw new MissingMethodException("NetTransferModeExtensions.ToChannelId");

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, MethodInfo> GenericCache = new();

    /// <summary>Broadcast a message to all connected guests with the phantom's id as wire sender.</summary>
    public static void BroadcastAsPhantom(ulong phantomNetId, INetMessage message)
    {
        if (RunManager.Instance?.NetService is not NetHostGameService host || host.ConnectedPeers.Count == 0)
        {
            return; // loopback: no guests to inform
        }
        int channel = (int)ToChannelId.Invoke(null, new object[] { message.Mode })!;
        MethodInfo generic = GenericCache.GetOrAdd(message.GetType(),
            t => SendToClientInternal.MakeGenericMethod(t));
        foreach (var peer in host.ConnectedPeers)
        {
            generic.Invoke(host, new object[] { message, peer.peerId, channel, phantomNetId });
        }
        Observability.EventLog.Write("mod", "forged_broadcast",
            new { from = phantomNetId, type = message.GetType().Name, peers = host.ConnectedPeers.Count });
        Log.Info($"[AiBuddy] Forged {message.GetType().Name} from phantom {phantomNetId} to {host.ConnectedPeers.Count} peer(s).");
    }
}
