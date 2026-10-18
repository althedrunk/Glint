using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Glint.Player;

public sealed partial class VolumeControl : Slider
{
    public event Action<int>? VolumeWheel;

    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        VolumeWheel?.Invoke(e.GetCurrentPoint(this).Properties.MouseWheelDelta);
        e.Handled = true;
    }
}
