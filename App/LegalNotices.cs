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

    public static async Task<bool> RequireAcceptanceAsync(FrameworkElement root, UserSettings settings)
    {
        if (settings.AcceptedThirdPartyTerms == Revision) return true;
        var termsPath = Path.Combine(Folder, "THIRD-PARTY-TERMS.txt");
        if (!File.Exists(termsPath)) throw new FileNotFoundException(L.T("许可文件缺失，请重新下载完整程序。"), termsPath);
        var panel = new StackPanel { Spacing = 12, MaxWidth = 620 };
        panel.Children.Add(new TextBlock { Text = L.T("主程序源码采用 MIT。随附的微软等第三方组件适用各自条款，请阅读后继续。"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new ScrollViewer { MaxHeight = 330, Content = new TextBlock {
            Text = File.ReadAllText(termsPath), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } });
        var files = new HyperlinkButton { Content = L.T("查看完整许可与第三方声明") };
        files.Click += (_, _) => OpenFolder(); panel.Children.Add(files);
        var dialog = new ContentDialog { Title = L.T("第三方组件许可"), Content = panel,
            PrimaryButtonText = L.T("同意并继续"), CloseButtonText = L.T("退出"),
            DefaultButton = ContentDialogButton.Primary, XamlRoot = root.XamlRoot, RequestedTheme = root.RequestedTheme };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        settings.AcceptedThirdPartyTerms = Revision; settings.Save();
        return true;
    }
}
