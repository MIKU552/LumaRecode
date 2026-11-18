using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace VideoConverter;

public sealed partial class MainWindow
{
    private async Task ConfigureToolsAsync()
    {
        if (activeJob is not null || probing || dialogOpen)
        {
            return;
        }

        dialogOpen = true;
        var panel = new StackPanel { Spacing = 16, MinWidth = 460 };
        panel.Children.Add(new TextBlock { Text = L.T("自动查找 PATH，也可以选择自己的 FFmpeg 和 ffprobe。"), TextWrapping = TextWrapping.Wrap });
        var fields = new Dictionary<string, TextBox>();
        var toolFields = new List<(string name, string value)> { ("ffmpeg", ffmpeg), ("ffprobe", ffprobe) };
        foreach (var (name, value) in toolFields)
        {
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new TextBox
            {
                Header = name + ".exe",
                Text = value,
                MinWidth = 340,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false
            };
            var button = new Button { Content = L.T("浏览…"), VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetColumn(button, 1);
            grid.Children.Add(box);
            grid.Children.Add(button);
            panel.Children.Add(grid);
            fields[name] = box;
            button.Click += async (_, _) =>
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".exe");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSingleFileAsync();
                if (file is null)
                {
                    return;
                }

                box.Text = file.Path;
                var peer = name == "ffmpeg" ? "ffprobe" : "ffmpeg";
                var candidate = Path.Combine(Path.GetDirectoryName(file.Path)!, peer + ".exe");
                if (fields[peer].Text == "" && File.Exists(candidate))
                {
                    fields[peer].Text = candidate;
                }
            };
        }
        var notice = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Error, IsOpen = false };
        panel.Children.Add(notice);
        var dialog = UiTypography.Prepare(new ContentDialog { Title = L.T("工具路径"), Content = panel, PrimaryButtonText = L.T("检测并保存"), CloseButtonText = L.T("取消"), XamlRoot = root.XamlRoot, RequestedTheme = root.RequestedTheme });
        if (sourceMedia is not null && ffmpeg.Length > 0 && ffprobe.Length > 0)
        {
            dialog.SecondaryButtonText = L.T("重新检测硬件编码器");
        }
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                foreach (var (name, box) in fields)
                {
                    await ConversionClient.CheckToolAsync(box.Text.Trim('"', ' '), name);
                }

                ffmpeg = fields["ffmpeg"].Text.Trim('"', ' ');
                ffprobe = fields["ffprobe"].Text.Trim('"', ' ');
                hardwareProbeTools = null;
                settings.Ffmpeg = ffmpeg;
                settings.Ffprobe = ffprobe;
                settings.Save();

            }
            catch (Exception error) { e.Cancel = true; notice.Message = L.T(error.Message); notice.IsOpen = true; }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        finally { dialogOpen = false; Refresh(); }
        if (!closing && result == ContentDialogResult.Secondary)
        {
            hardwareProbeTools = null;
            probing = true;
            Refresh();
            try
            {
                await InspectHardwareAsync();
                SetStatus("已识别素材，默认选项已就绪");
            }
            catch (OperationCanceledException) when (closing) { }
            finally
            {
                probing = false;
                if (!closing)
                {
                    Refresh();
                }
            }
            return;
        }
        if (!closing && sourceMedia is not null && hardwareProbeTools is null)
        {
            // Decoder support and format descriptors belong to the selected
            // tool build. Re-inspect rather than retaining the old snapshot.
            await InspectAsync(force: true);
        }
    }
    private async Task<bool> ConfirmOverwriteAsync(string output)
    {
        if (closing || dialogOpen)
        {
            return false;
        }
        dialogOpen = true;
        try
        {
            var dialog = UiTypography.Prepare(new ContentDialog
            {
                Title = L.T("覆盖已有文件？"),
                Content = L.T($"输出文件已存在：{output}\n编码和校验成功后，将替换此文件。"),
                PrimaryButtonText = L.T("覆盖"),
                CloseButtonText = L.T("取消"),
                PrimaryButtonStyle = (Style)Application.Current.Resources["DefaultButtonStyle"],
                CloseButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = root.XamlRoot,
                RequestedTheme = root.RequestedTheme
            });
            return await dialog.ShowAsync() == ContentDialogResult.Primary && !closing;
        }
        catch (OperationCanceledException) when (closing) { return false; }
        finally { dialogOpen = false; }
    }

    private async Task MessageAsync(string title, string message)
    {
        if (closing || dialogOpen)
        {
            return;
        }

        dialogOpen = true;
        try
        {
            await UiTypography.Prepare(new ContentDialog { Title = L.T(title), Content = L.T(message), CloseButtonText = L.T("确定"), XamlRoot = root.XamlRoot, RequestedTheme = root.RequestedTheme }).ShowAsync();
        }
        finally { dialogOpen = false; }
    }
}
