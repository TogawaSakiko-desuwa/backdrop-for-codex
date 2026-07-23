using System.Windows.Input;

namespace BackdropForCodex.App.Models;

/// <summary>
/// Translates pointer and keyboard input into normalized wallpaper focus changes.
/// The calculations stay independent of a live WPF window so interaction semantics can
/// be verified without starting the UI.
/// </summary>
public static class MediaFocusInput
{
    public static bool TryNormalizePointer(
        double pointerX,
        double pointerY,
        double viewportWidth,
        double viewportHeight,
        out MediaFocusPoint focus)
    {
        focus = default;
        if (!double.IsFinite(pointerX) ||
            !double.IsFinite(pointerY) ||
            !IsPositiveFinite(viewportWidth) ||
            !IsPositiveFinite(viewportHeight))
        {
            return false;
        }

        focus = new MediaFocusPoint(
            Math.Clamp(pointerX / viewportWidth, 0, 1),
            Math.Clamp(pointerY / viewportHeight, 0, 1));
        return true;
    }

    public static bool TryGetKeyboardDelta(
        Key key,
        ModifierKeys modifiers,
        out MediaFocusDelta delta)
    {
        var step = (modifiers & ModifierKeys.Shift) != 0 ? 0.10 : 0.01;
        delta = key switch
        {
            Key.Left => new MediaFocusDelta(-step, 0),
            Key.Right => new MediaFocusDelta(step, 0),
            Key.Up => new MediaFocusDelta(0, -step),
            Key.Down => new MediaFocusDelta(0, step),
            _ => default,
        };

        return delta != default;
    }

    private static bool IsPositiveFinite(double value) =>
        double.IsFinite(value) && value > 0;
}

public readonly record struct MediaFocusPoint(double X, double Y);

public readonly record struct MediaFocusDelta(
    double Horizontal,
    double Vertical);
