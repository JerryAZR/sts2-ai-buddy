using System.Reflection;
using AiBuddy.Phantom;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AiBuddy.Patches;

/// <summary>
/// M6 resume: re-inject phantom players into a LoadRunLobby. Vanilla populates
/// LoadRunLobby.Players from live connections only (never from the save), so a
/// resumed run would miss every phantom — and the begin-gate popup would report
/// them as "missing players". Postfixing AddLocalHostPlayer runs once per host
/// resume, immediately after lobby construction and before any guest can join:
/// injected phantoms ride the ClientLoadJoinResponseMessage.playersAlreadyConnected
/// list to guests for free. isReady=true is mandatory (IsAboutToBeginGame requires
/// all-ready). Guests who connect still get the peer-input forge at run start
/// (AutomationTick), same as fresh runs.
/// </summary>
[HarmonyPatch(typeof(LoadRunLobby), nameof(LoadRunLobby.AddLocalHostPlayer))]
internal static class LoadLobbyPhantomPatch
{
    private static void Postfix(LoadRunLobby __instance)
    {
        // Resume boundary = fresh session (same rule as LobbyPhantomPatch).
        PhantomSession session = PhantomSession.StartNew(__instance.NetService);

        ulong localId = __instance.NetService.NetId;
        bool hostIsModded = __instance.NetService.LocalVersion.IsModded();
        foreach (SerializablePlayer saved in __instance.Run.Players)
        {
            if (saved.NetId == localId || !PhantomSession.IsPhantomRangeId(saved.NetId))
            {
                continue;
            }
            var player = new LoadRunLobbyPlayer
            {
                id = saved.NetId,
                isReady = true,
                isModded = hostIsModded,
            };
            // Mirror the reconnecting-client path (HandleClientLoadJoinRequestMessage):
            // list add + listener notification. No broadcast — guests learn the roster
            // from playersAlreadyConnected when they join.
            __instance.Players.Add(player);
            __instance.LobbyListener.PlayerConnected(player);
            PhantomDescriptor descriptor = session.RegisterPhantom($"Buddy {saved.NetId}", saved.NetId);
            PhantomLobbyService.SeedPeerInput(__instance.InputSynchronizer, saved.NetId);
            Observability.EventLog.Write("mod", "phantom_resumed", new { descriptor.NetId });
            Log.Info($"[AiBuddy] Resumed phantom '{descriptor.DisplayName}' (NetId {descriptor.NetId}) from save.");
        }
    }
}

/// <summary>
/// TESTBENCH ONLY: with the autopilot enabled, ready up the host on the multiplayer
/// load screen so a headless `--fastmp load` resume embarks without UI input
/// (injected phantoms are already ready; all-ready triggers the begin).
/// </summary>
[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), nameof(NMultiplayerLoadGameScreen.InitializeAsHost))]
internal static class LoadLobbyAutoplayPatch
{
    private static readonly FieldInfo RunLobbyField =
        typeof(NMultiplayerLoadGameScreen).GetField("_runLobby", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException("NMultiplayerLoadGameScreen._runLobby");

    private static void Postfix(NMultiplayerLoadGameScreen __instance)
    {
        if (System.Environment.GetEnvironmentVariable("AIBUDDY_AUTOPLAY_LOCAL") != "1")
        {
            return;
        }
        var lobby = (LoadRunLobby)RunLobbyField.GetValue(__instance)!;
        Log.Info("[AiBuddy] Autopilot: readying host on load screen, resuming.");
        lobby.SetReady(true);
    }
}
