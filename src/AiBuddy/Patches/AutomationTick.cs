using AiBuddy.Drivers;
using AiBuddy.Phantom;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
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

    // Driver errors are escalated to the game log once per distinct failure —
    // catching is required (never throw out of _Process), but silence hides stalls.
    private static readonly HashSet<string> _reportedErrors = new();

    private static void Postfix(double delta)
    {
        try
        {
            Tick(delta);
        }
        catch (Exception ex)
        {
            ReportError("tick", ex);
        }
    }

    private static void Tick(double delta)
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
        if (run.NetService.Type == NetGameType.Host && PhantomSession.Current is { } session)
        {
            foreach (PhantomDescriptor phantom in session.Phantoms)
            {
                Player? player = state.GetPlayer(phantom.NetId);
                if (player != null)
                {
                    SafeDrive("EventDriver", () => EventDriver.Drive(player, state));
                    SafeDrive("MapDriver", () => MapDriver.Drive(player, state));
                }
            }
        }

        if (AutoplayLocal)
        {
            EnsureLocalSelector();
            Player? me = LocalContext.GetMe(state);
            if (me != null)
            {
                SafeDrive("EventDriver(local)", () => EventDriver.Drive(me, state));
                SafeDrive("MapDriver(local)", () => MapDriver.Drive(me, state));
            }
        }

        TraceEventStates(run);
    }

    private static void SafeDrive(string driverName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ReportError(driverName, ex);
        }
    }

    private static void ReportError(string source, Exception ex)
    {
        Observability.EventLog.Write("mod", "driver_error", new { source, error = ex.Message });
        string key = $"{source}|{ex.GetType().Name}|{ex.Message}";
        if (_reportedErrors.Add(key))
        {
            Log.Warn($"[AiBuddy] Driver '{source}' failed: {ex.GetType().Name}: {ex.Message} (further identical errors suppressed)");
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
            if (ev.IsFinished)
            {
                continue; // finished events linger until the next BeginEvent; not interesting
            }
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
