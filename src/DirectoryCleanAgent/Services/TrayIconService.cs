using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Services;

/// <summary>
/// 原生 Windows 系统托盘图标服务 — 基于 Shell_NotifyIcon P/Invoke。
/// 用于后台等待 Everything 索引完成时最小化到系统托盘，索引完成后通知用户。
///
/// 支持：
/// - 创建托盘图标并显示提示文本
/// - 右键菜单（显示主界面 / 退出）
/// - 双击托盘图标恢复窗口
/// - Windows 气泡通知
/// </summary>
public class TrayIconService : IDisposable
{
    private readonly ILogger<TrayIconService> _logger;
    private readonly nint _windowHandle;
    private readonly uint _callbackMessage;

    private bool _isVisible;
    private bool _disposed;

    // Win32 常量
    private const uint WM_APP = 0x8000;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIIF_INFO = 0x00000001;

    // 气泡通知事件（Shell_NotifyIcon 通过 lParam 低字发回）
    private const uint NIN_BALLOONUSERCLICK = 0x0405; // 用户点击气泡通知
    private const uint NIN_BALLOONTIMEOUT = 0x0404;   // 气泡通知超时消失

    // 自定义托盘消息
    private const uint WM_TRAYICON = WM_APP + 100;
    private const uint WM_TRAY_RBUTTONUP = 0x0205; // WM_RBUTTONUP
    private const uint WM_TRAY_LBUTTONDBLCLK = 0x0203; // WM_LBUTTONDBLCLK

    /// <summary>托盘图标左键双击事件（恢复窗口）</summary>
    public event EventHandler? TrayIconDoubleClick;

    /// <summary>托盘右键"显示主界面"事件</summary>
    public event EventHandler? ShowMainWindowRequested;

    /// <summary>托盘右键"退出"事件</summary>
    public event EventHandler? ExitRequested;

    /// <summary>用户点击托盘气泡通知事件（用于"查看结果"交互）</summary>
    public event EventHandler? BalloonClicked;

    public TrayIconService(ILogger<TrayIconService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _windowHandle = GetMainWindowHandle();
        _callbackMessage = WM_TRAYICON;
    }

    /// <summary>托盘图标是否可见</summary>
    public bool IsVisible => _isVisible;

    /// <summary>
    /// 显示托盘图标。
    /// </summary>
    /// <param name="tooltip">鼠标悬停提示文本</param>
    /// <param name="balloonTitle">气泡通知标题（可选）</param>
    /// <param name="balloonText">气泡通知内容（可选）</param>
    public void Show(string tooltip, string? balloonTitle = null, string? balloonText = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TrayIconService));

        try
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _windowHandle,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = _callbackMessage,
                hIcon = LoadDefaultIcon(),
                szTip = TruncateString(tooltip, 128)
            };

            // 气泡通知
            if (!string.IsNullOrEmpty(balloonText))
            {
                nid.uFlags |= NIF_INFO;
                nid.szInfoTitle = TruncateString(balloonTitle ?? string.Empty, 64);
                nid.szInfo = TruncateString(balloonText, 256);
                nid.dwInfoFlags = NIIF_INFO;
            }

            if (!Shell_NotifyIcon(NIM_ADD, ref nid))
            {
                _logger.LogWarning("Shell_NotifyIcon(NIM_ADD) 失败, 错误码: {Error}", Marshal.GetLastWin32Error());
            }
            else
            {
                _isVisible = true;
                _logger.LogInformation("托盘图标已创建");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建托盘图标异常");
        }
    }

    /// <summary>
    /// 显示 Windows 气泡通知。
    /// </summary>
    /// <param name="title">通知标题</param>
    /// <param name="text">通知内容</param>
    public void ShowBalloon(string title, string text)
    {
        if (!_isVisible || _disposed)
            return;

        try
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _windowHandle,
                uID = 1,
                uFlags = NIF_INFO,
                szInfoTitle = TruncateString(title, 64),
                szInfo = TruncateString(text, 256),
                dwInfoFlags = NIIF_INFO
            };

            Shell_NotifyIcon(NIM_MODIFY, ref nid);
            _logger.LogInformation("托盘气泡通知: {Title}", title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "显示托盘气泡通知异常");
        }
    }

    /// <summary>更新悬停提示文本</summary>
    public void UpdateTooltip(string tooltip)
    {
        if (!_isVisible || _disposed)
            return;

        try
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _windowHandle,
                uID = 1,
                uFlags = NIF_TIP,
                szTip = TruncateString(tooltip, 128)
            };

            Shell_NotifyIcon(NIM_MODIFY, ref nid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新托盘提示异常");
        }
    }

    /// <summary>移除托盘图标</summary>
    public void Hide()
    {
        if (!_isVisible || _disposed)
            return;

        try
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _windowHandle,
                uID = 1
            };

            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _isVisible = false;
            _logger.LogInformation("托盘图标已移除");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "移除托盘图标异常");
        }
    }

    /// <summary>
    /// 创建右键上下文菜单。
    /// 使用 Win32 TrackPopupMenu + CreatePopupMenu 实现原生右键菜单。
    /// </summary>
    public void ShowContextMenu(int x, int y)
    {
        try
        {
            nint menu = CreatePopupMenu();

            // 添加"显示主界面"菜单项
            AppendMenu(menu, 0x00000000, 1, "显示主界面");
            // 添加"退出"菜单项
            AppendMenu(menu, 0x00000000, 2, "退出");

            // 设置菜单项为前台活动窗口
            SetForegroundWindow(_windowHandle);

            // 显示弹出菜单
            uint selected = TrackPopupMenu(
                menu,
                0x0002 | 0x0100, // TPM_RETURNCMD | TPM_RIGHTBUTTON
                x, y, 0,
                _windowHandle, IntPtr.Zero);

            DestroyMenu(menu);

            // 处理菜单选择
            switch (selected)
            {
                case 1:
                    ShowMainWindowRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case 2:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "显示托盘上下文菜单异常");
        }
    }

    /// <summary>
    /// 处理窗口消息（需从 WPF 窗口的 WndProc 或 HwndSource 调用）。
    /// </summary>
    public void HandleMessage(int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != _callbackMessage || wParam != 1)
            return;

        uint lowEvent = (uint)(lParam & 0xFFFF);

        switch (lowEvent)
        {
            case WM_TRAY_LBUTTONDBLCLK: // 左键双击
                _logger.LogDebug("托盘图标双击");
                TrayIconDoubleClick?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case WM_TRAY_RBUTTONUP: // 右键抬起
                _logger.LogDebug("托盘图标右键");
                // 获取鼠标位置并显示菜单
                if (GetCursorPos(out POINT pt))
                {
                    ShowContextMenu(pt.X, pt.Y);
                }
                handled = true;
                break;

            case NIN_BALLOONUSERCLICK: // 用户点击气泡通知 → "查看结果"
                _logger.LogDebug("托盘气泡通知被用户点击");
                BalloonClicked?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case NIN_BALLOONTIMEOUT: // 气泡通知超时自动消失（仅记录，无需操作）
                _logger.LogTrace("托盘气泡通知超时消失");
                break;
        }
    }

    // ================================================================
    // 私有辅助方法
    // ================================================================

    /// <summary>获取默认应用程序图标句柄</summary>
    private static nint LoadDefaultIcon()
    {
        // 使用系统 Application 图标（IDI_APPLICATION = 32512）
        return LoadIcon(IntPtr.Zero, (nint)32512);
    }

    /// <summary>获取 WPF 主窗口的 Win32 句柄</summary>
    private static nint GetMainWindowHandle()
    {
        if (Application.Current?.MainWindow != null)
        {
            return new System.Windows.Interop.WindowInteropHelper(
                Application.Current.MainWindow).Handle;
        }
        return IntPtr.Zero;
    }

    /// <summary>截断字符串到指定长度（以 null 结尾）</summary>
    private static string TruncateString(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text.Length >= maxLength ? text[..(maxLength - 1)] : text;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Hide();
    }

    // ================================================================
    // Win32 P/Invoke 声明
    // ================================================================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool AppendMenu(nint hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern uint TrackPopupMenu(nint hMenu, uint uFlags, int x, int y,
        int nReserved, nint hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
