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
            bundleScreen = NRun.Instance?.GetTree().Root is { } root ? FindAll<NChooseABundleSelectionScreen>(root).FirstOrDefault() : null;
        }
        if (bundleScreen == null)
        {
            return;
        }

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
