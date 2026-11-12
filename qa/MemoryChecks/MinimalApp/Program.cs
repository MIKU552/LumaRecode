using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FrameFlowFloor;

internal static class Program {
 [STAThread] static void Main() {
  WinRT.ComWrappersSupport.InitializeComWrappers();
  Application.Start(parameters => {
   SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
   _ = new FloorApp();
  });
 }
}
public sealed partial class FloorApp:Application {
 private Window window;
 public FloorApp(){InitializeComponent();}
 protected override void OnLaunched(LaunchActivatedEventArgs args) {
  window=new Window{Title="WinUI memory floor (diagnostic)"};
  var root=new Grid{RequestedTheme=ElementTheme.Dark,Background=new SolidColorBrush(Color.FromArgb(255,30,30,30))};
  root.Children.Add(new SystemBackdropElement{SystemBackdrop=new MicaBackdrop(),IsHitTestVisible=false});
  var panel=new StackPanel{Margin=new Thickness(32),Spacing=20};
  panel.Children.Add(new TextBlock{Text="WinUI 原生界面内存对照",FontSize=30});
  TextBox input=null;
  if(Environment.ProcessPath.Contains("Textbox",StringComparison.OrdinalIgnoreCase)) {
   input=new TextBox{PlaceholderText="C:\\视频路径",IsSpellCheckEnabled=false,IsTextPredictionEnabled=false};
   panel.Children.Add(input);
   panel.Children.Add(new TextBox{PlaceholderText="输出路径",IsSpellCheckEnabled=false,IsTextPredictionEnabled=false});
  }
  root.Children.Add(panel);window.Content=root;
  var hwnd=WinRT.Interop.WindowNative.GetWindowHandle(window);
  double dpi=GetDpiForWindow(hwnd)/96d;
  var work=Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.AppWindow.Id,Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
  int width=(int)Math.Min(1120*dpi,work.Width-64*dpi),height=(int)Math.Min(900*dpi,work.Height-64*dpi);
  window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(work.X+(work.Width-width)/2,work.Y+(work.Height-height)/2,width,height));
  if(input is not null)root.Loaded+=(_,_)=>input.Focus(FocusState.Programmatic);
  window.Activate();
 }
 [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern uint GetDpiForWindow(IntPtr hwnd);
}
