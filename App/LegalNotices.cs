using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VideoConverter;

public static class LegalNotices
{
    public const string Revision = "windows-runtime-terms-2026-11-12-mit";
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "licenses");

    public static void OpenFolder()
    {
        if (!Directory.Exists(Folder)) throw new DirectoryNotFoundException(L.T("许可文件缺失，请重新下载完整程序。"));
        Process.Start(new ProcessStartInfo(Folder) { UseShellExecute = true });
    }

    public static async Task<bool> RequireAcceptanceAsync(FrameworkElement root, UserSettings settings, Action? languageChanged = null)
    {
        if (settings.AcceptedThirdPartyTerms == Revision) return true;
        var terms = new Dictionary<string, string>();
        foreach (var (code, _) in LanguagePolicy.Choices)
        {
            var path = Path.Combine(Folder, LanguagePolicy.TermsFile(code));
            if (!File.Exists(path)) throw new FileNotFoundException(L.T("许可文件缺失，请重新下载完整程序。"), path);
            terms[code] = File.ReadAllText(path);
        }
        var panel = new StackPanel { Spacing = 16, MaxWidth = 620 };
        var languageRow = new Grid { ColumnSpacing = 16 };
        languageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        languageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var languageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var language = new ComboBox { MinWidth = 152, HorizontalAlignment = HorizontalAlignment.Stretch };
        var system = new ComboBoxItem { Tag = "system" };
        language.Items.Add(system);
        foreach (var (code, name) in LanguagePolicy.Choices)
            language.Items.Add(new ComboBoxItem { Tag = code, Content = name });
        language.SelectedItem = language.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == settings.Language) ?? system;
        Grid.SetColumn(language, 1);
        languageRow.Children.Add(languageLabel); languageRow.Children.Add(language);
        panel.Children.Add(languageRow);
        var introduction = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var explanation = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 };
        panel.Children.Add(introduction);
        panel.Children.Add(new ScrollViewer { MaxHeight = Math.Clamp(root.ActualHeight - 360, 120, 330),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = body });
        panel.Children.Add(explanation);
        var files = new HyperlinkButton { Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        files.Click += (_, _) => OpenFolder(); panel.Children.Add(files);
        var dialog = new ContentDialog { Content = panel,
            DefaultButton = ContentDialogButton.Primary, XamlRoot = root.XamlRoot, RequestedTheme = root.RequestedTheme };
        var refreshing = false;
        void ApplyLanguage()
        {
            refreshing = true;
            try
            {
                dialog.Language = LanguagePolicy.XamlLanguage(L.Code);
                dialog.Title = L.T("第三方组件许可");
                dialog.PrimaryButtonText = L.T("同意并继续");
                dialog.CloseButtonText = L.T("退出");
                // Keep the first-run language entry discoverable in either locale.
                languageLabel.Text = L.Code is "ja" or "es" ? L.T("界面语言") + " / Language" : "界面语言 / Language";
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(language, languageLabel.Text);
                system.Content = L.T("跟随系统");
                var selected = language.SelectedIndex;
                language.SelectedIndex = -1; language.SelectedIndex = selected;
                introduction.Text = L.T("主程序源码采用 MIT。随附的微软等第三方组件适用各自条款，请阅读后继续。");
                body.Text = terms[L.Code];
                explanation.Text = L.T("此页为组件条款说明。完整许可按原文保留，请一并阅读。");
                files.Content = L.T("查看完整许可与第三方声明");
            }
            finally { refreshing = false; }
        }
        ApplyLanguage();
        language.SelectionChanged += (_, _) =>
        {
            if (refreshing || language.SelectedItem is not ComboBoxItem { Tag: string selection } || selection == settings.Language) return;
            settings.Language = selection; settings.Save(); L.Configure(selection);
            languageChanged?.Invoke();
            ApplyLanguage();
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        settings.AcceptedThirdPartyTerms = Revision; settings.Save();
        return true;
    }
}
