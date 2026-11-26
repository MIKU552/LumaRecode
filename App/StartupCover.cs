using System.Runtime.InteropServices;
using Windows.UI;

namespace VideoConverter;

/// <summary>Paints a native placeholder while WinUI composes the first window frame.</summary>
internal sealed class StartupCover : IDisposable
{
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsPopup = 0x80000000;
    private const uint UlwAlpha = 2;
    private const int SwShowNoActivate = 4;
    private const uint ImageIcon = 1;
    private const uint LoadIconFromFile = 0x10;
    private const uint DrawNormalIcon = 3;
    // Leave a tiny contribution from the owner so that DWM keeps composing it.
    private const byte InitialOpacity = 254;
    private nint handle;
    private readonly Size size;
    private nint memoryDc;
    private nint bitmap;
    private nint previousBitmap;

    private StartupCover(nint handle, Size size)
    {
        this.handle = handle;
        this.size = size;
    }

    internal static StartupCover? TryShow(nint owner, Color color, int iconSize)
    {
        if (GetWindowRect(owner, out var bounds) == 0 ||
            bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            return null;
        }

        var size = new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        var popup = CreateWindowExW(WsExNoActivate | WsExLayered | WsExToolWindow | WsExTransparent,
            "Static", null, WsPopup, bounds.Left, bounds.Top,
            size.Width, size.Height, owner, 0, 0, 0);
        if (popup == 0)
        {
            return null;
        }

        var cover = new StartupCover(popup, size);
        if (!cover.Paint(color, iconSize))
        {
            cover.Dispose();
            return null;
        }

        SetTransitionsDisabled(popup, true);
        _ = ShowWindow(popup, SwShowNoActivate);
        return cover;
    }

    internal static bool SetTransitionsDisabled(nint window, bool disabled)
    {
        const uint dwmTransitionsForceDisabled = 3;
        int value = disabled ? 1 : 0;
        return DwmSetWindowAttribute(window, dwmTransitionsForceDisabled, ref value, sizeof(int)) >= 0;
    }

    private bool Paint(Color color, int iconSize)
    {
        var screen = GetDC(0);
        if (screen == 0)
        {
            return false;
        }

        nint brush = 0;
        try
        {
            memoryDc = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, size.Width, size.Height);
            brush = CreateSolidBrush((uint)(color.R | color.G << 8 | color.B << 16));
            if (memoryDc == 0 || bitmap == 0 || brush == 0)
            {
                return false;
            }

            previousBitmap = SelectObject(memoryDc, bitmap);
            var client = new Rect { Right = size.Width, Bottom = size.Height };
            if (previousBitmap == 0 || previousBitmap == -1 || FillRect(memoryDc, ref client, brush) == 0)
            {
                return false;
            }

            var icon = LoadImageW(0, Path.Combine(AppContext.BaseDirectory, "LumaRecode.ico"),
                ImageIcon, iconSize, iconSize, LoadIconFromFile);
            if (icon != 0)
            {
                _ = DrawIconEx(memoryDc, (size.Width - iconSize) / 2, (size.Height - iconSize) / 2,
                    icon, iconSize, iconSize, 0, 0, DrawNormalIcon);
                _ = DestroyIcon(icon);
            }

            return SetOpacity(1);
        }
        finally
        {
            if (brush != 0) { _ = DeleteObject(brush); }
            _ = ReleaseDC(0, screen);
        }
    }

    internal bool SetOpacity(double opacity)
    {
        var dimensions = size;
        var source = new Point();
        var blend = new BlendFunction { SourceConstantAlpha = (byte)Math.Round(InitialOpacity * opacity) };
        return UpdateLayeredWindow(handle, 0, 0, ref dimensions, memoryDc, ref source, 0, ref blend, UlwAlpha) != 0;
    }

    public void Dispose()
    {
        if (handle == 0)
        {
            return;
        }

        _ = DestroyWindow(handle);
        handle = 0;
        if (previousBitmap != 0 && previousBitmap != -1) { _ = SelectObject(memoryDc, previousBitmap); }
        if (bitmap != 0) { _ = DeleteObject(bitmap); }
        if (memoryDc != 0) { _ = DeleteDC(memoryDc); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size(int width, int height) { public int Width = width, Height = height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint CreateWindowExW(uint extended, string className, string? title, uint style,
        int x, int y, int width, int height, nint owner, nint menu, nint module, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ShowWindow(nint window, int command);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DestroyWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint CreateSolidBrush(uint colorRef);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint SelectObject(nint dc, nint value);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int DeleteObject(nint value);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int DeleteDC(nint dc);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int FillRect(nint dc, ref Rect rect, nint brush);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint LoadImageW(nint module, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DrawIconEx(nint dc, int x, int y, nint icon, int width, int height,
        uint step, nint brush, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DestroyIcon(nint icon);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int UpdateLayeredWindow(nint window, nint destinationDc, nint destination,
        ref Size size, nint sourceDc, ref Point source, uint key, ref BlendFunction blend, uint flags);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, uint size);
}
