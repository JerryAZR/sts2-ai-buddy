using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;

namespace AiBuddy.Observability;

/// <summary>
/// Logs every network message sent or dispatched on this peer.
/// Pure observation — never alters message flow.
///
/// Outbound is tapped at the transport's byte-level SendMessageToClient
/// (non-generic; the typed SerializeMessage&lt;T&gt; is generic and unpatchable).
/// Frame format: [1 byte typeId][8 byte senderId LE][payload].
/// SendMessageToAll fans out through SendMessageToClient, so only the
/// latter is patched; a broadcast appears as N outbound lines.
/// Inbound is tapped at dispatch (not deserialization) so buffered-then-released
/// messages are logged exactly once, at the moment they take effect.
/// </summary>
[HarmonyPatch]
internal static class WireTap
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamHost), nameof(SteamHost.SendMessageToClient))]
    private static void OutboundSteam(ulong peerId, byte[] bytes, int length, NetTransferMode mode, int channel)
        => LogOutbound("steam", peerId, bytes, length, mode, channel);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ENetHost), nameof(ENetHost.SendMessageToClient))]
    private static void OutboundENet(ulong peerId, byte[] bytes, int length, NetTransferMode mode, int channel)
        => LogOutbound("enet", peerId, bytes, length, mode, channel);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NetMessageBus), nameof(NetMessageBus.SendMessageToAllHandlers))]
    private static void Inbound(INetMessage message, ulong senderId)
    {
        EventLog.Write("wire", "in", new
        {
            type = message.GetType().Name,
            sender = senderId,
        });
    }

    private static void LogOutbound(string transport, ulong peerId, byte[] bytes, int length, NetTransferMode mode, int channel)
    {
        string typeName = "?";
        ulong sender = 0;
        if (length >= 9)
        {
            typeName = MessageTypes.TryGetMessageType(bytes[0], out Type? type) && type != null
                ? type.Name
                : $"unknown:{bytes[0]}";
            sender = BitConverter.ToUInt64(bytes, 1);
        }
        EventLog.Write("wire", "out", new
        {
            transport,
            peer = peerId,
            type = typeName,
            sender,
            length,
            mode = mode.ToString(),
            channel,
        });
    }
}
