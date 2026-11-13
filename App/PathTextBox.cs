using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
        var opticalMargin = (Thickness)Application.Current.Resources["ControlTextOpticalMargin"];
        foreach (var name in new[] { "ContentElement", "PlaceholderTextContentPresenter" })
        {
            if (GetTemplateChild(name) is not FrameworkElement part) continue;
            part.VerticalAlignment = VerticalAlignment.Center;
            // Move the editor (including caret/selection) and placeholder together,
            // using the same optical offset as adjacent and footer button labels.
            part.RenderTransform = new TranslateTransform { Y = opticalMargin.Top };
        }
    }
}
