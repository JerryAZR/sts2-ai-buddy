using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace AiBuddy;

/// <summary>
/// Mod entry point. The game invokes the method named in the attribute
/// (static, parameterless) after loading the mod assembly.
///
/// Two patch sets: CORE (the phantom machinery — patched in every build; without
/// it the mod is a no-op) and OBSERVABILITY (debug builds only). Reflection targets
/// and patch landings are validated eagerly: EA signature drift must fail at mod
/// load with a clear message, never mid-run inside the game loop.
/// </summary>
[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public const string ModId = "AiBuddy";
    private const string HarmonyId = "aibuddy";

    private static Harmony? _harmony;

    private static readonly Type[] CorePatches =
    {
        typeof(Patches.LobbyPhantomPatch),
        typeof(Patches.CombatSyncPhantomPatch),
        typeof(Patches.PhantomActionEnqueuePatch),
        typeof(Patches.ChoiceFabricationPatch),
        typeof(Patches.CardPromptCapturePatches),
        typeof(Patches.RelicPromptCapturePatch),
        typeof(Patches.MendPromptCapturePatch),
        typeof(Patches.CardRewardPromptCapturePatch),
        typeof(Patches.RewardsSetOfferPatch),
        typeof(Patches.ChoiceResumePatch),
        typeof(Patches.RestSiteCapturePatch),
        typeof(Patches.PhantomSendGuardPatch),
        typeof(Patches.PhantomEnemyTurnReadyPatch),
        typeof(Patches.PlayCardCancelPatch),
        typeof(Patches.CombatActButtonPatch),
        typeof(Patches.RoomExitHygienePatch),
        typeof(Patches.ShopPurchaseForgePatch),
        typeof(Patches.LoadLobbyPhantomPatch),
        typeof(Patches.LoadLobbyAutoplayPatch),
        typeof(Patches.SaveGuardPatch),
        typeof(Patches.AutomationTick),
    };

    private static readonly Type[] DebugPatches =
    {
        typeof(Observability.WireTap),
        typeof(Observability.WaitTracer),
        typeof(Patches.RoomExitDiagnostics),
        typeof(Drivers.TestEventSplicePatch),
        typeof(Drivers.TestEventGateBypassPatch),
        typeof(Drivers.TestEventUnknownRollPatch),
    };

    /// <summary>Classes whose static initializers resolve game members via reflection (?? throw).</summary>
    private static readonly Type[] ReflectionHolders =
    {
        typeof(Phantom.PhantomLobbyService),
        typeof(Patches.CombatSyncPhantomPatch),
        typeof(Patches.PhantomActionEnqueuePatch),
        typeof(Patches.PhantomEnemyTurnReadyPatch),
        typeof(Drivers.ChoiceDriver),
        typeof(Drivers.CombatStateProbe),
        typeof(Drivers.EventDriver),
        typeof(Patches.MendPromptCapturePatch),
        typeof(Phantom.PhantomNet),
        typeof(Drivers.RestSiteDriver),
        typeof(Drivers.TreasureDriver),
        typeof(Drivers.OverlayDrainDriver),
        typeof(Drivers.TestEventSplicePatch),
        typeof(Drivers.TestEventGateBypassPatch),
        typeof(Drivers.TestEventUnknownRollPatch),
        typeof(Patches.RestSiteCapturePatch),
        typeof(Patches.LoadLobbyAutoplayPatch),
        typeof(Drivers.CrystalSphereDriver),
    };

    /// <summary>Game methods every build expects to have patched (DeclaringType.MethodName).</summary>
    private static readonly string[] ExpectedCoreTargets =
    {
        "NCharacterSelectScreen.InitializeMultiplayerAsHost",
        "CombatStateSynchronizer.StartSync",
        "ActionQueueSynchronizer.RequestEnqueue",
        "PlayerChoiceSynchronizer.WaitForRemoteChoice",
        "SteamHost.SendMessageToClient",
        "ENetHost.SendMessageToClient",
        "NRun._Process",
        "CardSelectCmd.FromChooseACardScreen",
        "CardSelectCmd.FromDeckGeneric",
        "CardSelectCmd.FromChooseABundleScreen",
        "ReadyToBeginEnemyTurnAction.ExecuteAction",
        "PlayCardAction.CancelAction",
        "GameActionPlayerChoiceContext.SignalPlayerChoiceEnded",
        "RestSiteSynchronizer.BeginRestSite",
        "RewardsSetSynchronizer.BeforeLeavingRoom",
        "NCombatRoom._Ready",
        "RewardSynchronizer.SyncLocalGoldLost",
        "RewardSynchronizer.SyncLocalObtainedCard",
        "RewardSynchronizer.SyncLocalObtainedRelic",
        "RewardSynchronizer.SyncLocalObtainedPotion",
        "LoadRunLobby.AddLocalHostPlayer",
        "NMultiplayerLoadGameScreen.InitializeAsHost",
        "RunSaveManager.RenameBrokenMultiplayerRunSave",
    };

    public static void Initialize()
    {
#if AI_BUDDY_DEBUG
        Observability.EventLog.Open();
        Log.Info($"[{ModId}] Initializing (debug). Event log: {Observability.EventLog.CurrentPath}");
#else
        Log.Info($"[{ModId}] Initializing (release).");
#endif

        ValidateReflectionTargets();

        _harmony = new Harmony(HarmonyId);
        foreach (Type patch in CorePatches)
        {
            _harmony.CreateClassProcessor(patch).Patch();
        }
#if AI_BUDDY_DEBUG
        foreach (Type patch in DebugPatches)
        {
            _harmony.CreateClassProcessor(patch).Patch();
        }
#endif

        VerifyCorePatchesLanded();
        Log.Info($"[{ModId}] Initialized. Core patches: {CorePatches.Length} classes verified.");
    }

    /// <summary>Force every reflection holder's static ctor now, so a renamed game member fails here. Internal: unit tests run this against the current game assembly to catch EA drift without launching the game.</summary>
    internal static void ValidateReflectionTargets()
    {
        foreach (Type holder in ReflectionHolders)
        {
            RuntimeHelpers.RunClassConstructor(holder.TypeHandle);
        }
    }

    private static void VerifyCorePatchesLanded()
    {
        var patched = _harmony!.GetPatchedMethods()
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .ToHashSet();
        var missing = ExpectedCoreTargets.Where(t => !patched.Contains(t)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"[{ModId}] Expected Harmony targets were not patched: {string.Join(", ", missing)}. " +
                "Game update likely changed signatures; refusing to load rather than fail mid-run.");
        }
    }
}
