using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace VideoConverter;

/// <summary>Keeps wheel and touchpad scrolling inside the native multiline log viewer.</summary>
public sealed partial class LogTextBox : TextBox
{
    public LogTextBox()
    {
        DefaultStyleKey = typeof(TextBox);
        // The template's ScrollViewer handles the wheel first. Consume the
        // routed event at this boundary even when its scroll limit was reached.
        AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler((_, args) => args.Handled = true), true);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("ContentElement") is ScrollViewer viewer)
        {
            // Precision-touchpad gestures use scroll chaining rather than the
            // wheel event. Reapply these settings whenever the template changes.
            viewer.IsVerticalScrollChainingEnabled = false;
            viewer.IsHorizontalScrollChainingEnabled = false;
        }
    }
}
