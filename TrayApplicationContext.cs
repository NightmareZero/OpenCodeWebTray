using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenCodeWebTray;

/// <summary>
/// 托盘应用主上下文：管理 opencode web 后台进程、托盘图标与交互。
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string OpencodeUrl = "http://127.0.0.1:4096/";
    private const string OpencodeArgs = "web --port 4096";

    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _iconOn;
    private readonly Icon _iconOff;

    private readonly ToolStripMenuItem _miStart;
    private readonly ToolStripMenuItem _miStop;
    private readonly ToolStripMenuItem _miExit;

    private readonly System.Windows.Forms.Timer _clickTimer;
    private readonly int _clickTimeout; // 系统双击判定时间，用于区分单击/双击
    private int _clickCount;

    private Process _process;
    private volatile bool _isRunning;   // opencode 后台进程是否运行中
    private bool _isExiting;            // 本程序是否正在退出

    public TrayApplicationContext()
    {
        _iconOn = LoadIcon("OpenCodeWebTray.Assets.opencode.ico");
        _iconOff = LoadIcon("OpenCodeWebTray.Assets.opencode-gray.ico");

        _miStart = new ToolStripMenuItem("开启");
        _miStop = new ToolStripMenuItem("关闭");
        _miExit = new ToolStripMenuItem("Exit");
        _miStart.Click += (_, _) => StartOpencode();
        _miStop.Click += (_, _) => StopOpencode();
        _miExit.Click += (_, _) => ExitApp();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_miStart);
        menu.Items.Add(_miStop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miExit);

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
            Text = "OpenCode Web Tray",
        };
        _notifyIcon.MouseClick += NotifyIcon_MouseClick;
        _notifyIcon.MouseDoubleClick += NotifyIcon_MouseDoubleClick;

        // 单击/双击判定：用系统双击时间，确保不超过系统阈值
        _clickTimeout = SystemInformation.DoubleClickTime;
        _clickTimer = new System.Windows.Forms.Timer { Interval = Math.Max(150, _clickTimeout) };
        _clickTimer.Tick += ClickTimer_Tick;

        // 启动时后台启动 opencode web
        StartOpencode();
        UpdateState();
    }

    private static Icon LoadIcon(string resourceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new InvalidOperationException("缺少嵌入资源：" + resourceName);
        return new Icon(stream);
    }

    // ---------- 鼠标交互 ----------

    private void NotifyIcon_MouseClick(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _clickCount++;
        if (_clickCount == 1)
        {
            _clickTimer.Stop();
            _clickTimer.Start(); // 启动延迟判定
        }
    }

    private void NotifyIcon_MouseDoubleClick(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        // 双击：取消单击的"打开网页"动作，改为切换开关
        _clickTimer.Stop();
        _clickCount = 0;
        ToggleOpencode();
    }

    private void ClickTimer_Tick(object sender, EventArgs e)
    {
        _clickTimer.Stop();
        if (_clickCount >= 1)
        {
            _clickCount = 0;
            // 计时器到期且期间没有发生双击 -> 视为单击：打开网页
            OpenUrl(OpencodeUrl);
        }
    }

    private void ToggleOpencode()
    {
        if (_isRunning) StopOpencode();
        else StartOpencode();
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 忽略打开失败
        }
    }

    // ---------- 进程管理 ----------

    private void StartOpencode()
    {
        if (_isExiting) return;
        if (_isRunning || _process != null)
        {
            UpdateState();
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "opencode",
                Arguments = OpencodeArgs,
                UseShellExecute = false,   // 不通过 shell，可隐藏窗口
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += OnProcessExited;
            _process.Start();
            _isRunning = true;
        }
        catch (Exception ex)
        {
            _process = null;
            _isRunning = false;
            MessageBox.Show(
                "无法启动 opencode：\n\n" + ex.Message +
                "\n\n请确认 opencode 已安装并在 PATH 中（在终端执行 `opencode --version` 验证）。",
                "OpenCode Web Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        UpdateState();
    }

    private void OnProcessExited(object sender, EventArgs e)
    {
        // Process.Exited 在线程池线程触发，需切回 UI 线程更新界面
        try { _process?.Dispose(); } catch { }
        _process = null;
        _isRunning = false;

        if (_isExiting) return;

        var scm = SynchronizationContext.Current;
        if (scm != null)
        {
            scm.Post(_ => UpdateState(), null);
        }
        else
        {
            try { _notifyIcon.ContextMenuStrip?.BeginInvoke((Action)UpdateState); } catch { }
        }
    }

    private void StopOpencode()
    {
        if (_process == null || !_isRunning)
        {
            UpdateState();
            return;
        }

        try
        {
            _process.Exited -= OnProcessExited;
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true); // 连同子进程一并终止
                    _process.WaitForExit(3000);
                }
                catch (InvalidOperationException)
                {
                    // 进程刚好已退出，忽略
                }
            }
        }
        catch
        {
            // 忽略终止过程中的异常
        }
        finally
        {
            try { _process?.Dispose(); } catch { }
            _process = null;
            _isRunning = false;
        }

        UpdateState();
    }

    // ---------- 状态同步 ----------

    private void UpdateState()
    {
        if (_notifyIcon == null) return;
        _notifyIcon.Icon = _isRunning ? _iconOn : _iconOff;
        _notifyIcon.Text = _isRunning ? "OpenCode Web (运行中)" : "OpenCode Web (已停止)";
        _miStart.Enabled = !_isRunning;
        _miStop.Enabled = _isRunning;
    }

    private void ExitApp()
    {
        _isExiting = true;
        StopOpencode();
        _notifyIcon.Visible = false; // 先隐藏，避免托盘残留幽灵图标
        _notifyIcon.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _isExiting = true;
            _clickTimer?.Stop();
            _clickTimer?.Dispose();
            try
            {
                if (_process != null && !_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch { }
            try { _process?.Dispose(); } catch { }
            _notifyIcon?.Dispose();
            _iconOn?.Dispose();
            _iconOff?.Dispose();
        }
        base.Dispose(disposing);
    }
}
