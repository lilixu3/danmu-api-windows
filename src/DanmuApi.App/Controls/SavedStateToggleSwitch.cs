using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DanmuApi.App.Controls;

/// <summary>A command-only switch: checked state is exclusively the verified saved-state binding.</summary>
public sealed class SavedStateToggleSwitch : ToggleSwitch
{
    private IPointer? _pressedPointer;

    public SavedStateToggleSwitch()
    {
        // ToggleSwitch's thumb drag path sets IsChecked directly (it does not call Toggle).
        // Guard input before it reaches the thumb; ordinary clicks/drags invoke the same command once.
        AddHandler(PointerPressedEvent, GuardPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, GuardPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, GuardPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, GuardCaptureLost);
    }

    protected override Type StyleKeyOverride => typeof(ToggleSwitch);

    protected override void Toggle()
    {
        // Keyboard/automation click paths may execute Command, but never change the saved-state value.
    }

    private void GuardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEffectivelyEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressedPointer = e.Pointer;
        Focus();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void GuardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (ReferenceEquals(e.Pointer, _pressedPointer)) e.Handled = true;
    }

    private void GuardPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, _pressedPointer)) return;
        _pressedPointer = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (IsEffectivelyEnabled && new Rect(Bounds.Size).Contains(e.GetPosition(this))) OnClick();
    }

    private void GuardCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _pressedPointer = null;
}
