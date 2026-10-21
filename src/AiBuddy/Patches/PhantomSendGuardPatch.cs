using AiBuddy.Phantom;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;

namespace AiBuddy.Patches;

/// <summary>
/// Defensive guard (design §3.7): targeted sends to a phantom id throw in the
/// transport (no connection exists). Drop them silently instead — the phantom
/// receives everything it needs via broadcast and host-local injection.
/// WireTap (same methods, observation-only) still logs what was attempted.
/// </summary>
[HarmonyPatch]
internal static class PhantomSendGuardPatch
{
    private static bool Guard(ulong peerId)
    {
        if (PhantomSession.Current?.IsPhantom(peerId) == true)
        {
            Observability.EventLog.Write("mod", "targeted_send_dropped", new { peerId });
            return false; // skip the transport send entirely
        }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamHost), nameof(SteamHost.SendMessageToClient))]
    private static bool Steam(ulong peerId) => Guard(peerId);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ENetHost), nameof(ENetHost.SendMessageToClient))]
    private static bool ENet(ulong peerId) => Guard(peerId);
}
