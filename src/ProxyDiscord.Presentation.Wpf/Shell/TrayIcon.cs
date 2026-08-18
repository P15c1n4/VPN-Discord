using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ProxyDiscord.Presentation.Wpf.Shell;

internal sealed class TrayIcon : IDisposable
{
    private const int TRAY_ICON_ID = 1;
    private const int MENU_OPEN_ID = 1;
    private const int MENU_EXIT_ID = 2;
    private const string TASKBAR_CREATED_MESSAGE = "TaskbarCreated";
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly HwndSource _messageWindow;
    private readonly int _taskbarCreatedMessage;
    private readonly string _tooltip;

    private IntPtr _icon;
    private bool _added;
    private bool _disposed;

    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip;
        _taskbarCreatedMessage = NativeShell.RegisterWindowMessageW(TASKBAR_CREATED_MESSAGE);

        _messageWindow = new HwndSource(new HwndSourceParameters(nameof(TrayIcon))
        {
            ParentWindow = HWND_MESSAGE,
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
        });
        _messageWindow.AddHook(OnMessage);

        _icon = LoadIcon();
        _added = AddOrUpdate(NativeShell.NIM_ADD);
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    private static IntPtr LoadIcon()
    {
        if (!AppIcons.AppIconExists)
        {
            return IntPtr.Zero;
        }

        var width = NativeShell.GetSystemMetrics(NativeShell.SM_CXSMICON);
        var height = NativeShell.GetSystemMetrics(NativeShell.SM_CYSMICON);

        return NativeShell.LoadImageW(
            IntPtr.Zero,
            AppIcons.AppIconPath,
            NativeShell.IMAGE_ICON,
            width,
            height,
            NativeShell.LR_LOADFROMFILE | NativeShell.LR_DEFAULTSIZE);
    }

    private bool AddOrUpdate(int message)
    {
        var data = new NativeShell.NotifyIconData
        {
            CbSize = Marshal.SizeOf<NativeShell.NotifyIconData>(),
            Hwnd = _messageWindow.Handle,
            UId = TRAY_ICON_ID,
            UFlags = NativeShell.NIF_MESSAGE | NativeShell.NIF_ICON | NativeShell.NIF_TIP,
            UCallbackMessage = NativeShell.WM_APP_TRAY,
            HIcon = _icon,
            SzTip = _tooltip,
            SzInfo = string.Empty,
            SzInfoTitle = string.Empty,
        };

        return NativeShell.Shell_NotifyIconW(message, ref data);
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            _added = AddOrUpdate(NativeShell.NIM_ADD);
            handled = true;
            return IntPtr.Zero;
        }

        if (message != NativeShell.WM_APP_TRAY)
        {
            return IntPtr.Zero;
        }

        switch ((int)lParam)
        {
            case NativeShell.WM_LBUTTONUP:
            case NativeShell.WM_LBUTTONDBLCLK:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case NativeShell.WM_RBUTTONUP:
                ShowContextMenu();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        var menu = NativeShell.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeShell.AppendMenuW(menu, NativeShell.MF_STRING, new IntPtr(MENU_OPEN_ID), "Abrir");
            NativeShell.AppendMenuW(menu, NativeShell.MF_SEPARATOR, IntPtr.Zero, null);
            NativeShell.AppendMenuW(menu, NativeShell.MF_STRING, new IntPtr(MENU_EXIT_ID), "Fechar");

            if (!NativeShell.GetCursorPos(out var cursor))
            {
                return;
            }

            NativeShell.SetForegroundWindow(_messageWindow.Handle);

            var command = NativeShell.TrackPopupMenuEx(
                menu,
                NativeShell.TPM_RIGHTBUTTON | NativeShell.TPM_RETURNCMD,
                cursor.X,
                cursor.Y,
                _messageWindow.Handle,
                IntPtr.Zero);

            NativeShell.PostMessageW(_messageWindow.Handle, NativeShell.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case MENU_OPEN_ID:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case MENU_EXIT_ID:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        finally
        {
            NativeShell.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added)
        {
            AddOrUpdate(NativeShell.NIM_DELETE);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            NativeShell.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        _messageWindow.RemoveHook(OnMessage);
        _messageWindow.Dispose();
    }
}
