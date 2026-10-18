using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Glint;

// A Grid that shows the horizontal-resize cursor; drag handling lives in MainWindow.
public sealed partial class GripSplitter : Grid
{
    public GripSplitter()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
