using System.Windows.Input;
using BackdropForCodex.App.Models;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class MediaFocusInputTests
{
    [Fact]
    public void PointerCoordinatesAreNormalizedAndClampedToTheViewport()
    {
        Assert.True(
            MediaFocusInput.TryNormalizePointer(
                pointerX: 50,
                pointerY: 25,
                viewportWidth: 200,
                viewportHeight: 100,
                out var inside));
        Assert.Equal(0.25, inside.X);
        Assert.Equal(0.25, inside.Y);

        Assert.True(
            MediaFocusInput.TryNormalizePointer(
                pointerX: -20,
                pointerY: 150,
                viewportWidth: 200,
                viewportHeight: 100,
                out var capturedOutside));
        Assert.Equal(0, capturedOutside.X);
        Assert.Equal(1, capturedOutside.Y);
    }

    [Theory]
    [InlineData(100, 100, 0, 100)]
    [InlineData(100, 100, double.NaN, 100)]
    [InlineData(double.PositiveInfinity, 100, 100, 100)]
    public void InvalidPointerGeometryIsIgnored(
        double pointerX,
        double pointerY,
        double viewportWidth,
        double viewportHeight)
    {
        Assert.False(
            MediaFocusInput.TryNormalizePointer(
                pointerX,
                pointerY,
                viewportWidth,
                viewportHeight,
                out _));
    }

    [Theory]
    [InlineData(Key.Left, ModifierKeys.None, -0.01, 0)]
    [InlineData(Key.Right, ModifierKeys.Control, 0.01, 0)]
    [InlineData(Key.Up, ModifierKeys.Shift, 0, -0.10)]
    [InlineData(
        Key.Down,
        ModifierKeys.Control | ModifierKeys.Shift,
        0,
        0.10)]
    public void ArrowKeysUseOnePercentOrShiftAcceleratedTenPercentSteps(
        Key key,
        ModifierKeys modifiers,
        double expectedHorizontal,
        double expectedVertical)
    {
        Assert.True(
            MediaFocusInput.TryGetKeyboardDelta(
                key,
                modifiers,
                out var delta));
        Assert.Equal(expectedHorizontal, delta.Horizontal);
        Assert.Equal(expectedVertical, delta.Vertical);
    }

    [Fact]
    public void NonArrowKeysDoNotChangeFocus()
    {
        Assert.False(
            MediaFocusInput.TryGetKeyboardDelta(
                Key.Space,
                ModifierKeys.Shift,
                out var delta));
        Assert.Equal(default, delta);
    }
}
