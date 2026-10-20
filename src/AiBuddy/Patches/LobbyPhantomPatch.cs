using AiBuddy.Phantom;
using AiBuddy.Observability;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;

namespace AiBuddy.Patches;

/// <summary>
/// Wires phantom creation into the host's multiplayer character-select screen:
///  - a host-only "Add AI Buddy" button (the human path), and
///  - env-var auto-add (AIBUDDY_AUTO_PHANTOMS=N) for headless integration tests.
/// </summary>
[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.InitializeMultiplayerAsHost))]
internal static class LobbyPhantomPatch
{
    private const string ButtonName = "AiBuddyAddButton";

    private static void Postfix(NCharacterSelectScreen __instance)
    {
        // A fresh lobby means a fresh phantom set.
        PhantomRegistry.Clear();
        AttachButton(__instance);
        AutoAddFromEnvironment(__instance);
    }

    private static void AttachButton(NCharacterSelectScreen screen)
    {
        if (screen.GetNodeOrNull<Button>(ButtonName) != null)
        {
            return;
        }
        var button = new Button
        {
            Name = ButtonName,
            Text = "Add AI Buddy",
            // Crude placement for M1 — top-right corner. Polish lands in M8.
            Position = new Vector2(1640, 20),
            Size = new Vector2(220, 48),
        };
        button.Pressed += () =>
        {
            PhantomDescriptor? phantom = PhantomLobbyService.AddPhantom(screen.Lobby);
            if (phantom == null)
            {
                button.Text = "Lobby full";
                button.Disabled = true;
            }
        };
        screen.AddChild(button);
    }

    private static void AutoAddFromEnvironment(NCharacterSelectScreen screen)
    {
        string? raw = System.Environment.GetEnvironmentVariable("AIBUDDY_AUTO_PHANTOMS");
        if (!int.TryParse(raw, out int count) || count <= 0)
        {
            return;
        }
        Log.Info($"[AiBuddy] AIBUDDY_AUTO_PHANTOMS={count}: auto-adding phantoms for integration testing.");
        for (int i = 0; i < count; i++)
        {
            if (PhantomLobbyService.AddPhantom(screen.Lobby) == null)
            {
                break;
            }
        }

        // Full automation: with the autopilot enabled, ready up the host too —
        // BeginRunForAllPlayersIfAllReady embarks once every player is ready.
        if (System.Environment.GetEnvironmentVariable("AIBUDDY_AUTOPLAY_LOCAL") == "1")
        {
            Log.Info("[AiBuddy] Autopilot: readying host, embarking.");
            screen.Lobby.SetReady(true);
        }
    }
}
