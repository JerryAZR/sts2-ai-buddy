using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace AiBuddy;

/// <summary>
/// Mod entry point. The game invokes the method named in the attribute
/// (static, parameterless) after loading the mod assembly.
/// </summary>
[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public const string ModId = "AiBuddy";
    private const string HarmonyId = "aibuddy";

    public static void Initialize()
    {
        Log.Info($"[{ModId}] Initializing (debug={IsDebugBuild()}).");
        var harmony = new Harmony(HarmonyId);

#if AI_BUDDY_DEBUG
        Observability.EventLog.Open();
        harmony.PatchAll(typeof(Observability.WireTap).Assembly);
        Log.Info($"[{ModId}] Debug observability active. Event log: {Observability.EventLog.CurrentPath}");
#else
        // Release builds patch nothing yet — phantom logic lands in later milestones.
        _ = harmony;
#endif
    }

    private static bool IsDebugBuild()
    {
#if AI_BUDDY_DEBUG
        return true;
#else
        return false;
#endif
    }
}
