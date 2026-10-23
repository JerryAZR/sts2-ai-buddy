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
    public enum AddResult { Added, LobbyFull, InjectionFailed }

    private static readonly FieldInfo MaxPlayersField =
        typeof(StartRunLobby).GetField("_maxPlayers", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException("StartRunLobby._maxPlayers");

    private static readonly MethodInfo TryAddPlayer =
        typeof(StartRunLobby).GetMethod("TryAddPlayerInFirstAvailableSlot", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException("StartRunLobby.TryAddPlayerInFirstAvailableSlot");

    private static readonly MethodInfo UpdateMaxAscension =
        typeof(StartRunLobby).GetMethod("UpdateMaxMultiplayerAscension", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException("StartRunLobby.UpdateMaxMultiplayerAscension");

    /// <summary>Add one phantom to the lobby.</summary>
    public static (AddResult result, PhantomDescriptor? phantom) AddPhantom(StartRunLobby lobby, PhantomSession session)
    {
        int maxPlayers = (int)MaxPlayersField.GetValue(lobby)!;
        if (lobby.Players.Count >= maxPlayers)
        {
            Log.Info($"[AiBuddy] Lobby full ({lobby.Players.Count}/{maxPlayers}); not adding phantom.");
            Observability.EventLog.Write("mod", "phantom_add_rejected_full", new { players = lobby.Players.Count, maxPlayers });
            return (AddResult.LobbyFull, null);
        }

        // Probe allocation first; register only after the lobby accepts the player.
        ulong netId = session.PeekNextId();
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
            return (AddResult.InjectionFailed, null);
        }

        StartRunLobbyPlayer player = added.Value;
        PhantomDescriptor descriptor = session.RegisterPhantom(displayName, netId);

        // Notify the lobby UI exactly as a real join would.
        lobby.LobbyListener.PlayerConnected(player);
        UpdateMaxAscension.Invoke(lobby, null);

        // Phantoms join already ready (control layer owns mechanical confirmations).
        int index = lobby.Players.FindIndex(p => p.id == descriptor.NetId);
        if (index >= 0)
        {
            StartRunLobbyPlayer ready = lobby.Players[index];
            ready.isReady = true;
            lobby.Players[index] = ready;
            lobby.LobbyListener.PlayerChanged(ready, false);
        }

        // Tell any connected guests. Broadcast fans out to connected peers only,
        // so this is a safe no-op in a host-only loopback session.
        lobby.NetService.SendMessage(new PlayerJoinedMessage { lobbyPlayer = player });
        // The ready message carries no player id (sender is inferred) — it MUST be
        // forged with the phantom as wire sender, or guests misread it as the host.
        PhantomNet.BroadcastAsPhantom(descriptor.NetId, new LobbyPlayerSetReadyMessage { ready = true });

        Observability.EventLog.Write("mod", "phantom_added", new
        {
            netId = descriptor.NetId,
            descriptor.DisplayName,
            slotId = player.slotId,
            character = player.character?.Id.ToString(),
        });
        Log.Info($"[AiBuddy] Added phantom '{descriptor.DisplayName}' (NetId {descriptor.NetId}, slot {player.slotId}).");
        return (AddResult.Added, descriptor);
    }
}
