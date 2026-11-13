using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VideoConverter;

/// <summary>A single-line path field with centered text and placeholder in the native TextBox template.</summary>
public sealed partial class PathTextBox : TextBox
{
    public PathTextBox()
    {
        DefaultStyleKey = typeof(TextBox);
        MinHeight = 36;
        Padding = new Thickness(10, 6, 6, 6);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // The native template top-aligns its editing view and placeholder;
        // setting TextBox.VerticalContentAlignment alone does not bind these parts.
        // Center both parts so extra row height is shared above and below the text.
        if (GetTemplateChild("ContentElement") is FrameworkElement editor)
            editor.VerticalAlignment = VerticalAlignment.Center;
        if (GetTemplateChild("PlaceholderTextContentPresenter") is FrameworkElement placeholder)
            placeholder.VerticalAlignment = VerticalAlignment.Center;
    }
}
