using AiBuddy.Drivers;
using AiBuddy.Phantom;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
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
    private static int _tickCount;
    private static readonly bool AutoplayLocal =
        System.Environment.GetEnvironmentVariable("AIBUDDY_AUTOPLAY_LOCAL") == "1";

    // Driver errors are escalated to the game log once per distinct failure —
    // catching is required (never throw out of _Process), but silence hides stalls.
    private static readonly HashSet<string> _reportedErrors = new();
    private static ActionSynchronizerCombatState? _lastCombatState;
    private static MapCoord? _lastMapCoord;

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

        // Boundary edges: per-combat sequence state and per-room prompt captures must
        // not survive transitions (both drivers panic-log a non-empty clear).
        ActionSynchronizerCombatState combatState = run.ActionQueueSynchronizer.CombatState;
        if (_lastCombatState != combatState)
        {
            if (combatState is ActionSynchronizerCombatState.PlayPhase or ActionSynchronizerCombatState.NotInCombat)
            {
                CombatDriver.OnCombatBoundary();
            }
            if (combatState == ActionSynchronizerCombatState.NotInCombat)
            {
                ChoiceDriver.OnBoundary();
            }
            _lastCombatState = combatState;
        }
        MapCoord? coord = state.CurrentMapCoord;
        if (!Equals(_lastMapCoord, coord))
        {
            if (_lastMapCoord.HasValue)
            {
                ChoiceDriver.OnBoundary();
            }
            _lastMapCoord = coord;
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
                    SafeDrive("RewardsDriver", () => RewardsDriver.Drive(player, state));
                }
            }
            SafeDrive("CombatDriver", () => CombatDriver.DriveSequence(state));
        }

        if (AutoplayLocal)
        {
            EnsureLocalSelector();
            OverlayDrainDriver.Tick();
            DrainLocalRewards(run);
            Player? me = LocalContext.GetMe(state);
            if (me != null)
            {
                SafeDrive("EventDriver(local)", () => EventDriver.Drive(me, state));
                SafeDrive("MapDriver(local)", () => MapDriver.Drive(me, state));
                SafeDrive("CombatDriver(local)", () => CombatDriver.DriveAutoplay(me, state));
            }
        }

        NeowRelicTestDriver.Tick(state);
        CombatChoiceTestDriver.Tick(state);

        // Heartbeat every 10s so a silent tick is distinguishable from a quiet game.
        if (++_tickCount % 20 == 0)
        {
            Observability.EventLog.Write("wait", "tick_heartbeat", new
            {
                tick = _tickCount,
                combatState = run.ActionQueueSynchronizer.CombatState.ToString(),
                netType = run.NetService.Type.ToString(),
                phantoms = PhantomSession.Current?.Phantoms.Count ?? -1,
                playersInState = state.Players.Count,
                phantomPlayersResolved = PhantomSession.Current?.Phantoms
                    .Count(p => state.GetPlayer(p.NetId) != null) ?? -1,
            });
        }

        TraceEventStates(run);
    }

    /// <summary>Debug autopilot: the host's reward screen blocks map progress — drain it by skipping (same call the skip button makes).</summary>
    private static void DrainLocalRewards(RunManager run)
    {
        if (PhantomSession.Current?.LocalAutoplayRewardSet is not { } set)
        {
            return;
        }
        if (run.RewardsSetSynchronizer.IsRewardsSetCompleted(set))
        {
            PhantomSession.Current.LocalAutoplayRewardSet = null;
            return;
        }
        Observability.EventLog.Write("mod", "autopilot_rewards_skip", new { setId = set.Id });
        run.RewardsSetSynchronizer.SkipLocalRewardsSet();
        PhantomSession.Current.LocalAutoplayRewardSet = null;
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
