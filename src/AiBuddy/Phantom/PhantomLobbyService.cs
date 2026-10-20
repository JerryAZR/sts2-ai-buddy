using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace AiBuddy.Phantom;

/// <summary>
/// Injects phantom players into a host's StartRunLobby, replicating exactly what
/// a real client's join does: slot allocation, listener/UI notification, ready state,
/// and broadcast of the lobby messages guests use to build the identical player list.
/// </summary>
public static class PhantomLobbyService
{
    private static readonly FieldInfo MaxPlayersField =
        typeof(StartRunLobby).GetField("_maxPlayers", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException("StartRunLobby._maxPlayers");

    private static readonly MethodInfo TryAddPlayer =
        typeof(StartRunLobby).GetMethod("TryAddPlayerInFirstAvailableSlot", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException("StartRunLobby.TryAddPlayerInFirstAvailableSlot");

    private static readonly MethodInfo UpdateMaxAscension =
        typeof(StartRunLobby).GetMethod("UpdateMaxMultiplayerAscension", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException("StartRunLobby.UpdateMaxMultiplayerAscension");

    /// <summary>Add one phantom to the lobby. Returns null (and logs) if the lobby is full.</summary>
    public static PhantomDescriptor? AddPhantom(StartRunLobby lobby)
    {
        int maxPlayers = (int)MaxPlayersField.GetValue(lobby)!;
        if (lobby.Players.Count >= maxPlayers)
        {
            Log.Info($"[AiBuddy] Lobby full ({lobby.Players.Count}/{maxPlayers}); not adding phantom.");
            Observability.EventLog.Write("mod", "phantom_add_rejected_full", new { players = lobby.Players.Count, maxPlayers });
            return null;
        }

        ulong netId = PhantomRegistry.AllocateNextId();
        string displayName = $"Buddy {netId}";

        // Phantom mirrors the host's unlock state / ascension / moddedness so
        // lobby-wide calculations (max ascension) and content filtering match a real second copy of the host.
        var hostUnlockState = new UnlockState(SaveManager.Instance.Progress).ToSerializable();
        int hostMaxAscension = SaveManager.Instance.Progress.MaxMultiplayerAscension;
        bool hostIsModded = lobby.NetService.LocalVersion.IsModded();

        var added = (StartRunLobbyPlayer?)TryAddPlayer.Invoke(
            lobby, new object[] { hostUnlockState, hostMaxAscension, hostIsModded, netId });
        if (added == null)
        {
            Log.Warn($"[AiBuddy] TryAddPlayerInFirstAvailableSlot returned null for phantom {netId}.");
            return null;
        }

        StartRunLobbyPlayer player = added.Value;
        PhantomRegistry.Register(netId, displayName);

        // Notify the lobby UI exactly as a real join would.
        lobby.LobbyListener.PlayerConnected(player);
        UpdateMaxAscension.Invoke(lobby, null);

        // Phantoms join already ready (control layer owns mechanical confirmations).
        int index = lobby.Players.FindIndex(p => p.id == netId);
        if (index >= 0)
        {
            StartRunLobbyPlayer ready = lobby.Players[index];
            ready.isReady = true;
            lobby.Players[index] = ready;
            lobby.LobbyListener.PlayerChanged(ready, false);
        }

        // Tell any connected guests. Broadcast fans out to connected peers only,
        // so this is a safe no-op in a host-only loopback session.
        // KNOWN GAP (M7): sender id on these is the host's, not the phantom's —
        // forged-sender sends land with the Steam transport work.
        lobby.NetService.SendMessage(new PlayerJoinedMessage { lobbyPlayer = player });
        lobby.NetService.SendMessage(new LobbyPlayerSetReadyMessage { ready = true });

        Observability.EventLog.Write("mod", "phantom_added", new
        {
            netId,
            displayName,
            slotId = player.slotId,
            character = player.character?.Id.ToString(),
        });
        Log.Info($"[AiBuddy] Added phantom '{displayName}' (NetId {netId}, slot {player.slotId}).");
        return new PhantomDescriptor(netId, displayName);
    }
}
