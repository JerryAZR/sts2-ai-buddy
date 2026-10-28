using Godot;
using System.Reflection;
using AiBuddy.Phantom;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace AiBuddy.Drivers;

/// <summary>
/// Debug-autopilot overlay drain. The local reward UI is NOT a view of sim state —
/// nothing on these screens observes the backend, so sets answered elsewhere never
/// close themselves; the drain is the reconciliation point.
///
/// Structure: opinionated handlers only where the brain must decide (bundle pick,
/// card-reward pick from the stashed LocalCardPick); a GENERIC fallback clicks any
/// proceed/skip/confirm button on a screen that has sat unchanged for a few ticks —
/// unknown or orphaned screens self-drain instead of silently parking the run. A
/// stuck-screen watchdog warns when even the fallback finds nothing to click.
/// </summary>
public static class OverlayDrainDriver
{
    private static readonly HashSet<long> _selectedIn = new();
    private static string? _lastPeekType;
    private static int _unchangedTicks;
    private static bool _stuckReported;
    private static bool _gameOverReported;

    private static readonly string[] FallbackButtonNames = { "ProceedButton", "SkipButton", "ConfirmButton" };

    private static readonly MethodInfo TreasureOpenChest =
        typeof(NTreasureRoom).GetMethod("OpenChest", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException("NTreasureRoom.OpenChest");

    private static readonly FieldInfo CrystalSphereEntity =
        typeof(MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen).GetField("_entity", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException("NCrystalSphereScreen._entity");

    private static readonly Random CellRng = new();

    private static void DrainCrystalSphere(MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen screen)
    {
        var minigame = (MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame)CrystalSphereEntity.GetValue(screen)!;
        if (!minigame.IsFinished)
        {
            var hidden = new List<MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereCell>();
            foreach (var cell in minigame.cells)
            {
                if (cell.IsHidden)
                {
                    hidden.Add(cell);
                }
            }
            if (hidden.Count > 0)
            {
                MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(minigame.CellClicked(hidden[CellRng.Next(hidden.Count)]));
                Observability.EventLog.Write("mod", "autopilot_crystal_sphere_click", new { remaining = minigame.DivinationCount });
            }
            return;
        }
        if (ClickFirst(screen, "ProceedButton"))
        {
            Observability.EventLog.Write("mod", "autopilot_crystal_sphere_proceed");
        }
    }

    public static void Tick()
    {
        NOverlayStack? stack = NOverlayStack.Instance;
        Godot.Control? top = stack?.Peek() as Godot.Control;
        // Game over is terminal, not stuck: report once, stop driving.
        if (top is NGameOverScreen)
        {
            if (!_gameOverReported)
            {
                _gameOverReported = true;
                _lastPeekType = nameof(NGameOverScreen);
                Observability.EventLog.Write("mod", "autopilot_game_over");
                Observability.EventLog.Write("wait", "overlay_peek", new { top = nameof(NGameOverScreen) });
            }
            return;
        }
        TrackStuck(top);

        // The stashed card pick lives exactly as long as the reward screens do.
        if (top is not NCardRewardSelectionScreen and not NRewardsScreen
            && PhantomSession.Current is { LocalCardPick: not null } staleSession)
        {
            staleSession.LocalCardPick = null;
        }

        // Card reward screen: click the brain's pick, RETRYING each tick — the screen
        // eats clicks during its anti-misclick window after opening (observed: one
        // early ForceClick swallowed, screen waits forever). The click self-limits:
        // once the pick resolves, the screen closes and the stash clears above.
        if (top is NCardRewardSelectionScreen cardScreen && PhantomSession.Current?.LocalCardPick is { } pick)
        {
            NCardHolder? holder = cardScreen.GetCardHolder(pick);
            if (holder != null)
            {
                // NCardHolder.Pressed is emitted from real mouse input only — ForceClick
                // on the hitbox never reaches it. Emit the signal directly (the exact
                // call a real click ends in). Retried each tick: before the screen
                // awaits OptionSelected, SelectCard throws — caught and retried.
                try
                {
                    holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
                    Observability.EventLog.Write("mod", "autopilot_card_reward_clicked", new { card = pick.Id.Entry });
                }
                catch (Exception ex)
                {
                    Observability.EventLog.Write("mod", "autopilot_card_reward_click_failed", new { card = pick.Id.Entry, error = ex.Message });
                }
            }
            else
            {
                PhantomSession.Current!.LocalCardPick = null; // card no longer offered — resolved or gone
            }
            return;
        }

        // Rewards screen with the local set completed/skipped: proceed (close).
        if (top is NRewardsScreen && PhantomSession.Current?.LocalAutoplayRewardSet == null)
        {
            if (ClickFirst(top, "ProceedButton"))
            {
                Observability.EventLog.Write("mod", "autopilot_rewards_proceed");
            }
            return;
        }

        // Bundle screen (no selector seam): select first, confirm next tick.
        NChooseABundleSelectionScreen? bundleScreen = top as NChooseABundleSelectionScreen;
        if (bundleScreen == null)
        {
            bundleScreen = NRun.Instance?.GetTree().Root is { } root ? FindAll<NChooseABundleSelectionScreen>(root).FirstOrDefault() : null;
        }
        if (bundleScreen != null)
        {
            DrainBundle(bundleScreen);
            return;
        }

        // Crystal Sphere minigame (local autopilot): click a random hidden cell per
        // tick until divinations run out, then proceed. The game's AutoSlay handler
        // is dead on release builds, so we drive the model directly.
        if (top is MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen sphereScreen)
        {
            DrainCrystalSphere(sphereScreen);
            return;
        }

        // Generic fallback: any OTHER screen that lingers gets its proceed/skip/
        // confirm clicked (self-draining) — unknown screens can't park the run.
        if (top != null && _unchangedTicks >= 4)
        {
            TryGenericDrain(top);
        }

        // Rest-site room exit is a ROOM-level button (not an overlay): click proceed
        // once the local rest site is done. Clears when the room leaves the tree.
        if (PhantomSession.Current is { LocalRestSitePendingProceed: true } session
            && NRun.Instance?.GetTree().Root is { } sceneRoot)
        {
            NRestSiteRoom? room = FindAll<NRestSiteRoom>(sceneRoot).FirstOrDefault();
            if (room == null)
            {
                session.LocalRestSitePendingProceed = false;
            }
            else
            {
                // ChooseLocalOption bypasses the room UI's post-select flow, so proceed
                // never enables itself and map travel stays disabled (both normally done
                // by the button-path ShowProceedButton). Reconcile harness-side.
                if (!room.ProceedButton.IsEnabled)
                {
                    room.ProceedButton.Enable();
                }
                MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance?.SetTravelEnabled(true);
                Observability.EventLog.Write("mod", "autopilot_rest_proceed");
                room.ProceedButton.ForceClick();
                session.LocalRestSitePendingProceed = false; // one click; re-flag if that was premature
            }
        }

        // Merchant room exit (same room-level pattern as rest sites; travel is
        // enabled at room setup here, so a plain click suffices).
        if (PhantomSession.Current is { LocalShopPendingProceed: true } shopSession
            && NRun.Instance?.GetTree().Root is { } shopRoot)
        {
            NMerchantRoom? shop = FindAll<NMerchantRoom>(shopRoot).FirstOrDefault();
            if (shop == null)
            {
                shopSession.LocalShopPendingProceed = false;
            }
            else
            {
                if (!shop.ProceedButton.IsEnabled)
                {
                    shop.ProceedButton.Enable();
                }
                Observability.EventLog.Write("mod", "autopilot_shop_proceed");
                shop.ProceedButton.ForceClick();
                shopSession.LocalShopPendingProceed = false;
            }
        }

        // Treasure room exit (same room-level pattern as merchant).
        if (PhantomSession.Current is { LocalTreasurePendingProceed: true } treasureSession
            && NRun.Instance?.GetTree().Root is { } treasureRoot)
        {
            NTreasureRoom? treasure = FindAll<NTreasureRoom>(treasureRoot).FirstOrDefault();
            if (treasure == null)
            {
                treasureSession.LocalTreasurePendingProceed = false;
            }
            else
            {
                if (!treasure.ProceedButton.IsEnabled)
                {
                    treasure.ProceedButton.Enable();
                }
                Observability.EventLog.Write("mod", "autopilot_treasure_proceed");
                treasure.ProceedButton.ForceClick();
                treasureSession.LocalTreasurePendingProceed = false;
            }
        }

        // Treasure chest: the local chest must go through the UI flow (OpenChest) —
        // the relic display and the proceed button's Skip->Proceed switch hang off it.
        if (PhantomSession.Current is { } chestSession
            && RunManager.Instance?.DebugOnlyGetState() is { } chestState
            && chestState.CurrentRoom is MegaCrit.Sts2.Core.Rooms.TreasureRoom
            && LocalContext.GetMe(chestState) is { } chestMe
            && !chestSession.TreasureChestOpened.Contains(chestMe.NetId)
            && NRun.Instance?.GetTree().Root is { } chestRoot
            && FindAll<NTreasureRoom>(chestRoot).FirstOrDefault() is { } chestRoom)
        {
            chestSession.TreasureChestOpened.Add(chestMe.NetId);
            Observability.EventLog.Write("mod", "autopilot_treasure_chest");
            MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely((System.Threading.Tasks.Task)TreasureOpenChest.Invoke(chestRoom, null)!);
        }
    }

    private static void DrainBundle(NChooseABundleSelectionScreen bundleScreen)
    {
        long id = bundleScreen.GetInstanceId().GetHashCode(); // stable enough for a two-tick interaction
        if (!_selectedIn.Contains(id))
        {
            NCardBundle? bundle = FindAll<NCardBundle>(bundleScreen).FirstOrDefault();
            if (bundle?.Hitbox != null)
            {
                bundle.Hitbox.ForceClick();
                _selectedIn.Add(id);
                Observability.EventLog.Write("mod", "autopilot_bundle_selected");
            }
            else
            {
                Observability.EventLog.Write("mod", "autopilot_bundle_none_found", new { children = bundleScreen.GetChildCount() });
            }
            return;
        }
        NClickableControl? confirm = FindAll<NClickableControl>(bundleScreen)
            .FirstOrDefault(c => c.GetType().Name == nameof(NConfirmButton));
        if (confirm != null)
        {
            confirm.ForceClick();
            _selectedIn.Remove(id);
            Observability.EventLog.Write("mod", "autopilot_bundle_confirmed");
        }
    }

    /// <summary>Generic fallback: click the first proceed/skip/confirm-ish button on a lingering screen.</summary>
    private static void TryGenericDrain(Godot.Control screen)
    {
        foreach (string name in FallbackButtonNames)
        {
            if (ClickFirst(screen, name))
            {
                Observability.EventLog.Write("mod", "autopilot_generic_drain", new { screen = screen.GetType().Name, button = name });
                return;
            }
        }
    }

    private static bool ClickFirst(Godot.Control root, string buttonName)
    {
        NClickableControl? button = FindAll<NClickableControl>(root)
            .FirstOrDefault(c => c.GetType().Name == buttonName || c.Name == buttonName);
        if (button == null)
        {
            return false;
        }
        button.ForceClick();
        return true;
    }

    /// <summary>Stuck-screen watchdog: top-of-stack unchanged ~10s → loud warn (a park is never silent).</summary>
    private static void TrackStuck(Godot.Control? top)
    {
        string type = top?.GetType().Name ?? "<none>";
        if (type != _lastPeekType)
        {
            _lastPeekType = type;
            _unchangedTicks = 0;
            _stuckReported = false;
            Observability.EventLog.Write("wait", "overlay_peek", new { top = type });
            return;
        }
        if (top == null)
        {
            return;
        }
        if (++_unchangedTicks == 20 && !_stuckReported)
        {
            _stuckReported = true;
            MegaCrit.Sts2.Core.Logging.Log.Warn($"[AiBuddy] Overlay '{type}' stuck at top of stack for ~10s (autoplay).");
            Observability.EventLog.Write("wait", "overlay_stuck", new { top = type });
        }
    }

    /// <summary>Recursive is-T tree walk (AutoSlay UiHelper.FindAll pattern) — FindChildren's type filter doesn't match C# script classes.</summary>
    private static List<T> FindAll<T>(Node root) where T : Node
    {
        var found = new List<T>();
        void Walk(Node node)
        {
            if (node is T match)
            {
                found.Add(match);
            }
            foreach (Node child in node.GetChildren())
            {
                Walk(child);
            }
        }
        Walk(root);
        return found;
    }
}
