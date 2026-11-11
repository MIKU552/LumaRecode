using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace VideoConverter;

public sealed class Appearance
{
    private readonly FrameworkElement root;
    private readonly Window window;
    private readonly UserSettings settings;
    private readonly UISettings system = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly List<string> overridden = new();
    private readonly HdrBackdropCompatibility backdrop;
    private ComboBox? themeChoice;
    private ComboBox? materialChoice;
    private bool applying;
    private bool disposed;
    private bool highContrast;
    private Border? accentPreview;
    private TextBlock? accentLabel;
    private ToggleSwitch? systemSwitch;
    private ColorPicker? picker;
    private readonly List<(Button button, string hex)> swatches = new();

    public Appearance(FrameworkElement root, Window window, UserSettings settings)
    {
        this.root = root; this.window = window; this.settings = settings;
        window.Closed += (_, _) => disposed = true;
        backdrop = new HdrBackdropCompatibility((Grid)root, window, settings);
        ((Button)root.FindName("PaletteButton")).Flyout = BuildPalette();
        root.ActualThemeChanged += (_, _) => { if (!applying) ApplyAccent(false); UpdateCaption(); };
        system.ColorValuesChanged += (_, _) => root.DispatcherQueue.TryEnqueue(() => { ApplyAccent(false); UpdateCaption(); });
        // Some unpackaged desktop environments do not expose this WinRT event.
        // WinUI still follows high contrast; re-read it when the window is activated.
        try { accessibility.HighContrastChanged += (_, _) => root.DispatcherQueue.TryEnqueue(() => ApplyAccent(true)); }
        catch (System.Runtime.InteropServices.COMException) { }
        window.Activated += (_, _) =>
        {
            if (!disposed && highContrast != accessibility.HighContrast) ApplyAccent(true);
            UpdateCaption();
        };
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        applying = true;
        highContrast = accessibility.HighContrast;
        root.RequestedTheme = settings.Theme switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };
        if (themeChoice is not null)
            themeChoice.SelectedItem = themeChoice.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == settings.Theme) ?? themeChoice.Items[0];
        applying = false;
        ApplyAccent(true); UpdateCaption();
    }

    public void Relocalize()
    {
        swatches.Clear();
        ((Button)root.FindName("PaletteButton")).Flyout = BuildPalette();
        ApplyAccent(false);
    }

    private void ApplyAccent(bool refresh)
    {
        if (applying || disposed) return;
        applying = true;
        highContrast = accessibility.HighContrast;
        var hadOverrides = overridden.Count > 0;
        foreach (var key in overridden) { Application.Current.Resources.Remove(key); root.Resources.Remove(key); }
        overridden.Clear();
        var color = settings.SystemAccent ? system.GetColorValue(UIColorType.Accent) : Parse(settings.CustomColor);
        var dark = root.ActualTheme == ElementTheme.Dark;
        if (!settings.SystemAccent && !accessibility.HighContrast)
        {
            Put("SystemAccentColor", color);
            for (var i = 1; i <= 3; i++)
            {
                Put($"SystemAccentColorLight{i}", Mix(color, Colors.White, .17 * i));
                Put($"SystemAccentColorDark{i}", Mix(color, Colors.Black, .17 * i));
            }
            // Keep the primary action and its text readable for very light or dark custom colors.
            var foreground = Luminance(color) > .179 ? Colors.Black : Colors.White;
            Put("AccentFillColorDefaultBrush", new SolidColorBrush(color));
            Put("AccentFillColorSecondaryBrush", new SolidColorBrush(color) { Opacity = .9 });
            Put("AccentFillColorTertiaryBrush", new SolidColorBrush(color) { Opacity = .8 });
            Put("TextOnAccentFillColorPrimaryBrush", new SolidColorBrush(foreground));
            Put("TextOnAccentFillColorSecondaryBrush", new SolidColorBrush(foreground) { Opacity = .85 });
            var readable = dark && Luminance(color) < .2 ? Mix(color, Colors.White, .45)
                : !dark && Luminance(color) > .45 ? Mix(color, Colors.Black, .5) : color;
            Put("AccentTextFillColorPrimaryBrush", new SolidColorBrush(readable));
            // WinUI's default dictionary aliases these with StaticResource. Override
            // each public lightweight styling key so existing templates refresh too.
            foreach (var key in new[] { "AccentButtonBackground", "RadioButtonOuterEllipseCheckedStroke", "RadioButtonOuterEllipseCheckedFill",
                "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundFillChecked", "ToggleSwitchFillOn", "ToggleSwitchStrokeOn", "ProgressBarForeground" })
            {
                Put(key, new SolidColorBrush(color));
                Put(key + "PointerOver", new SolidColorBrush(color) { Opacity = .9 });
                Put(key + "Pressed", new SolidColorBrush(color) { Opacity = .8 });
            }
            foreach (var key in new[] { "AccentButtonForeground", "RadioButtonCheckGlyphFill", "CheckBoxCheckGlyphForegroundChecked", "ToggleSwitchKnobFillOn" })
            {
                Put(key, new SolidColorBrush(foreground));
                Put(key + "PointerOver", new SolidColorBrush(foreground));
                Put(key + "Pressed", new SolidColorBrush(foreground) { Opacity = .85 });
            }
        }
        if (refresh && (hadOverrides || overridden.Count > 0))
        {
            var theme = root.RequestedTheme;
            root.RequestedTheme = dark ? ElementTheme.Light : ElementTheme.Dark;
            root.RequestedTheme = theme;
        }
        if (accentPreview is not null) accentPreview.Background = new SolidColorBrush(color);
        if (accentLabel is not null) accentLabel.Text = (settings.SystemAccent ? L.T("Windows 强调色 · ") : L.T("自定义 · ")) + Hex(color);
        foreach (var (button, hex) in swatches)
        {
            button.BorderThickness = new Thickness(!settings.SystemAccent && hex.Equals(settings.CustomColor, StringComparison.OrdinalIgnoreCase) ? 3 : 1);
            button.BorderBrush = new SolidColorBrush(dark ? Colors.White : Colors.Black) { Opacity = !settings.SystemAccent && hex.Equals(settings.CustomColor, StringComparison.OrdinalIgnoreCase) ? .9 : .15 };
        }
        backdrop.Refresh();
        applying = false;
    }

    private void Put(string name, object value) { Application.Current.Resources[name] = value; root.Resources[name] = value; overridden.Add(name); }

    private Flyout BuildPalette()
    {
        var content = new StackPanel { Width = 300, Spacing = 16 };
        content.Children.Add(new TextBlock { Text = L.T("界面配色"), FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        themeChoice = new ComboBox { Header = L.T("主题模式"), HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(themeChoice, L.T("主题模式"));
        foreach (var (key, name) in new[] { ("system", L.T("跟随系统")), ("light", L.T("浅色")), ("dark", L.T("深色")) })
            themeChoice.Items.Add(new ComboBoxItem { Tag = key, Content = name });
        themeChoice.SelectedItem = themeChoice.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == settings.Theme) ?? themeChoice.Items[0];
        themeChoice.SelectionChanged += (_, _) =>
        {
            if (applying || themeChoice.SelectedItem is not ComboBoxItem { Tag: string key } || settings.Theme == key) return;
            settings.Theme = key; settings.Save(); ApplyTheme();
        };
        content.Children.Add(themeChoice);
        materialChoice = new ComboBox { Header = L.T("窗口材质"), HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(materialChoice, L.T("窗口材质"));
        materialChoice.Items.Add(new ComboBoxItem { Tag = "mica", Content = L.T("原生云母 · Mica") });
        materialChoice.Items.Add(new ComboBoxItem { Tag = "solid", Content = L.T("纯色 · 兼容模式") });
        materialChoice.SelectedIndex = settings.Backdrop == "solid" ? 1 : 0;
        materialChoice.SelectionChanged += (_, _) =>
        {
            if (applying || materialChoice.SelectedItem is not ComboBoxItem { Tag: string key } || settings.Backdrop == key) return;
            settings.Backdrop = key; settings.Save(); backdrop.Refresh();
        };
        content.Children.Add(materialChoice);
        content.Children.Add(new TextBlock { Text = L.T("保留系统云母观感；遇到显示异常时可切换纯色背景。"), TextWrapping = TextWrapping.Wrap, Opacity = .65, FontSize = 12 });
        var note = new TextBlock { Text = L.T("为按钮、选择状态和进度设置强调色。"), TextWrapping = TextWrapping.Wrap, Opacity = .65, FontSize = 13 };
        content.Children.Add(note);
        systemSwitch = new ToggleSwitch { Header = L.T("使用系统强调色"), IsOn = settings.SystemAccent, OnContent = L.T("跟随 Windows"), OffContent = L.T("自定义颜色") };
        content.Children.Add(systemSwitch);
        var preview = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        accentPreview = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(6) };
        accentLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        preview.Children.Add(accentPreview); preview.Children.Add(accentLabel); content.Children.Add(preview);
        var grid = new Grid { ColumnSpacing = 9, RowSpacing = 9 };
        for (int i = 0; i < 6; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 2; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var colors = new[] { "#0078D4", "#0067C0", "#5B5FC7", "#8764B8", "#C239B3", "#E3008C",
                            "#D13438", "#DA3B01", "#CA5010", "#038387", "#008272", "#107C10" };
        for (int i = 0; i < colors.Length; i++)
        {
            var hex = colors[i];
            var button = new Button { Background = new SolidColorBrush(Parse(hex)), Width = 41, Height = 34, CornerRadius = new CornerRadius(6) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, L.T("配色 ") + hex);
            ToolTipService.SetToolTip(button, hex);
            button.Click += (_, _) => ChooseColor(Parse(hex));
            swatches.Add((button, hex)); Grid.SetColumn(button, i % 6); Grid.SetRow(button, i / 6); grid.Children.Add(button);
        }
        content.Children.Add(grid);
        picker = new ColorPicker { Color = Parse(settings.CustomColor), IsAlphaEnabled = false, IsAlphaSliderVisible = false,
            IsAlphaTextInputVisible = false, IsHexInputVisible = true, IsColorChannelTextInputVisible = true,
            IsColorSliderVisible = true, IsColorSpectrumVisible = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, L.T("自定义强调色"));
        var advanced = new Expander { Header = L.T("自定义颜色"), Content = picker, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(advanced);
        var restore = new Button { Content = L.T("恢复默认外观"), HorizontalAlignment = HorizontalAlignment.Stretch };
        restore.Click += (_, _) =>
        {
            applying = true;
            settings.Theme = "system"; settings.Backdrop = "mica"; settings.SystemAccent = true; settings.CustomColor = "#0078D4";
            materialChoice.SelectedIndex = 0;
            systemSwitch.IsOn = true; picker.Color = Parse(settings.CustomColor);
            applying = false;
            settings.Save(); ApplyTheme();
        };
        content.Children.Add(restore);
        systemSwitch.Toggled += (_, _) => { if (applying) return; settings.SystemAccent = systemSwitch.IsOn; settings.Save(); ApplyAccent(true); };
        picker.ColorChanged += (_, e) => { if (!applying) ChooseColor(e.NewColor, false); };
        return new Flyout { Content = new ScrollViewer { Content = content, MaxHeight = 640,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
    }

    private void ChooseColor(Color color, bool syncPicker = true)
    {
        settings.CustomColor = Hex(color); settings.SystemAccent = false;
        applying = true;
        if (systemSwitch is not null) systemSwitch.IsOn = false;
        if (syncPicker && picker is not null) picker.Color = color;
        applying = false;
        settings.Save(); ApplyAccent(true);
    }

    private void UpdateCaption()
    {
        if (disposed) return;
        var dark = root.ActualTheme == ElementTheme.Dark;
        var bar = window.AppWindow?.TitleBar;
        if (bar is null) return;
        bar.ButtonBackgroundColor = Colors.Transparent; bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(255, 145, 145, 145) : Color.FromArgb(255, 110, 110, 110);
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(25, 0, 0, 0);
        bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
    }
    public static Color Parse(string text)
    {
        try { return Color.FromArgb(255, Convert.ToByte(text.Substring(1, 2), 16), Convert.ToByte(text.Substring(3, 2), 16), Convert.ToByte(text.Substring(5, 2), 16)); }
        catch { return Color.FromArgb(255, 0, 120, 212); }
    }
    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(255, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    private static double Luminance(Color color)
    {
        static double Linear(byte value) { double v = value / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}
