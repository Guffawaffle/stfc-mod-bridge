using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace STFCCommunityMod.Launcher.Controls;

public sealed class OverflowMenuButton : Button
{
    protected override void OnClick()
    {
        base.OnClick();
        if (ContextMenu is null)
        {
            return;
        }

        ContextMenu.PlacementTarget = this;
        ContextMenu.Placement = PlacementMode.Bottom;
        ContextMenu.IsOpen = true;
    }
}
