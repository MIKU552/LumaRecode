using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;

namespace VideoConverter;

/// <summary>Owns lazy registration and delivery through the Windows App SDK notification API.</summary>
internal sealed class JobNotifications(Action activate)
{
    private AppNotificationManager? manager;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    // The extracted application path changes between releases. A stable AppID
    // keeps notification settings and history under one Windows application.
    internal static void ConfigureIdentity() =>
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID("MIKU552.LumaRecode"));

    private void Register()
    {
        if (manager is not null)
        {
            return;
        }
        if (!AppNotificationManager.IsSupported())
        {
            throw new InvalidOperationException(L.T("Windows 通知当前不可用。"));
        }
        var candidate = AppNotificationManager.Default;
        candidate.NotificationInvoked += OnInvoked;
        try
        {
            candidate.Register("映码 · LumaRecode", new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "LumaRecode.ico")));
            manager = candidate;
        }
        catch
        {
            candidate.NotificationInvoked -= OnInvoked;
            throw;
        }
    }

    internal bool TryRegister(out string error)
    {
        try
        {
            Register();
            error = "";
            return true;
        }
        catch (Exception failure) when (failure is COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            error = failure.Message;
            return false;
        }
    }

    internal bool TryShow(string payload, out string error)
    {
        if (!TryRegister(out error))
        {
            return false;
        }
        try
        {
            var notification = new AppNotification(payload) { Group = "conversions", Tag = Guid.NewGuid().ToString("N")[..16] };
            manager!.Show(notification);
            if (notification.Id == 0)
            {
                error = L.T("Windows 未接受此通知。");
                return false;
            }
            error = "";
            return true;
        }
        catch (Exception failure) when (failure is COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Notification delivery is separate from the completed encode.
            // Surface delivery failures in the log without changing its outcome.
            error = failure.Message;
            return false;
        }
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => activate();

    internal void Close()
    {
        if (manager is null)
        {
            return;
        }
        manager.NotificationInvoked -= OnInvoked;
        try
        {
            manager.Unregister();
        }
        catch (COMException) { }
        manager = null;
    }
}
