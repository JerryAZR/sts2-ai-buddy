using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace AiBuddy.Drivers;

/// <summary>
/// Debug-autopilot overlay drain (AutoSlay screen-handler pattern): some local
/// prompts bypass the ICardSelector seam entirely (the bundle screen has no
/// selector check), so the autopilot answers them at the UI layer via ForceClick.
/// One interaction per tick: select first, confirm next tick (confirm is gated
/// on selection).
/// </summary>
public static class OverlayDrainDriver
{
    /// <summary>Screens we've clicked a bundle in but not yet confirmed (autoplay-only state).</summary>
    private static readonly HashSet<long> _selectedIn = new();
    private static string? _lastPeekType;

    public static void Tick()
    {
        NOverlayStack? stack = NOverlayStack.Instance;
        string peekType = stack?.Peek()?.GetType().Name ?? "<none>";
        if (peekType != _lastPeekType)
        {
            _lastPeekType = peekType;
            Observability.EventLog.Write("wait", "overlay_peek", new { top = peekType, count = stack?.ScreenCount ?? -1 });
        }

        // Primary: overlay stack. Fallback: scene-root search (some screens may not be stacked).
        NChooseABundleSelectionScreen? bundleScreen = stack?.Peek() as NChooseABundleSelectionScreen;
        if (bundleScreen == null)
        {
            bundleScreen = NRun.Instance?.GetTree().Root
                .FindChildren(nameof(NChooseABundleSelectionScreen), nameof(NChooseABundleSelectionScreen), true, false)
                .OfType<NChooseABundleSelectionScreen>().FirstOrDefault();
        }
        if (bundleScreen == null)
        {
            return;
        }

        long id = bundleScreen.GetInstanceId().GetHashCode(); // stable enough for a two-tick interaction
        if (!_selectedIn.Contains(id))
        {
            NCardBundle? bundle = bundleScreen.FindChildren("*", nameof(NCardBundle), true, false)
                .OfType<NCardBundle>().FirstOrDefault();
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
        NClickableControl? confirm = bundleScreen.FindChildren("*", nameof(NConfirmButton), true, false)
            .OfType<NClickableControl>().FirstOrDefault();
        if (confirm != null)
        {
            confirm.ForceClick();
            _selectedIn.Remove(id);
            Observability.EventLog.Write("mod", "autopilot_bundle_confirmed");
        }
    }
}
