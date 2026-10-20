using AiBuddy.Drivers;
using AiBuddy.Phantom;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Patches;

/// <summary>
/// 500ms automation tick (KitLib CombatSyncBotPatch pattern). Drives phantom
/// decision points and, when AIBUDDY_AUTOPLAY_LOCAL=1, the local player too
/// (debug test harness — replaces human clicking in integration tests).
/// </summary>
[HarmonyPatch(typeof(NRun), nameof(NRun._Process))]
internal static class AutomationTick
{
    private const double IntervalSeconds = 0.5;
    private static double _accumulator;
    private static readonly bool AutoplayLocal =
        System.Environment.GetEnvironmentVariable("AIBUDDY_AUTOPLAY_LOCAL") == "1";

    private static void Postfix(double delta)
    {
        _accumulator += delta;
        if (_accumulator < IntervalSeconds)
        {
            return;
        }
        _accumulator = 0;

        RunManager? run = RunManager.Instance;
        if (run?.NetService is not { Type: NetGameType.Host or NetGameType.Singleplayer })
        {
            return;
        }
        RunState? state = run.DebugOnlyGetState();
        if (state == null)
        {
            return;
        }

        // Phantom drivers run on the host only.
        if (run.NetService.Type == NetGameType.Host)
        {
            foreach (PhantomDescriptor phantom in PhantomRegistry.All)
            {
                Player? player = state.GetPlayer(phantom.NetId);
                if (player != null)
                {
                    SafeDrive(() => EventDriver.Drive(player), phantom.NetId);
                }
            }
        }

        TraceEventStates(run);

        if (AutoplayLocal)
        {
            EnsureLocalSelector();
            Player? me = LocalContext.GetMe(state);
            if (me != null)
            {
                SafeDrive(() => EventDriver.Drive(me), me.NetId);
            }
        }
    }

    /// <summary>
    /// The local selector stack is cleared by CardSelectCmd.Reset on run transitions,
    /// so reinstall on every tick — self-healing, no lifecycle tracking.
    /// </summary>
    private static void EnsureLocalSelector()
    {
        if (CardSelectCmd.LocalSelector == null)
        {
            CardSelectCmd.UseSelector(new AutoplayCardSelector(), localOnly: true);
        }
    }

    private static void SafeDrive(Action action, ulong? netId)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Observability.EventLog.Write("mod", "driver_error", new { netId, error = ex.Message });
        }
    }

    /// <summary>Per-tick event-state visibility: who has an event, finished?, page size, transition guard.</summary>
    private static void TraceEventStates(RunManager run)
    {
        var sync = run.EventSynchronizer;
        if (sync.Events.Count == 0)
        {
            return;
        }
        foreach (var ev in sync.Events)
        {
            Observability.EventLog.Write("wait", "event_state", new
            {
                netId = ev.Owner.NetId,
                eventId = ev.Id.Entry,
                finished = ev.IsFinished,
                options = ev.CurrentOptions.Count,
                anyChosen = ev.CurrentOptions.Any(o => o.WasChosen),
            });
        }
    }
}
