using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace AiBuddy.Observability;

/// <summary>
/// Logs every network message sent or dispatched on this peer.
/// Pure observation — never alters message flow.
/// Inbound is tapped at dispatch (not deserialization) so buffered-then-released
/// messages are logged exactly once, at the moment they take effect.
/// </summary>
[HarmonyPatch]
internal static class WireTap
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NetMessageBus), nameof(NetMessageBus.SerializeMessage))]
    private static void Outbound(ulong senderId, INetMessage message)
    {
        EventLog.Write("wire", "out", new
        {
            type = message.GetType().Name,
            sender = senderId,
            summary = Summarize(message),
        });
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NetMessageBus), nameof(NetMessageBus.SendMessageToAllHandlers))]
    private static void Inbound(INetMessage message, ulong senderId)
    {
        EventLog.Write("wire", "in", new
        {
            type = message.GetType().Name,
            sender = senderId,
            summary = Summarize(message),
        });
    }

    /// <summary>Cheap payload summary: public fields as key=value, truncated.</summary>
    private static string Summarize(INetMessage message)
    {
        try
        {
            var fields = message.GetType().GetFields()
                .Where(f => f.IsPublic && f.Name != nameof(INetMessage.ShouldBroadcast))
                .Take(6)
                .Select(f => $"{f.Name}={Truncate(f.GetValue(message))}");
            return string.Join(", ", fields);
        }
        catch
        {
            return "?";
        }
    }

    private static string Truncate(object? value)
    {
        string s = value?.ToString() ?? "null";
        return s.Length <= 80 ? s : s[..80] + "…";
    }
}
