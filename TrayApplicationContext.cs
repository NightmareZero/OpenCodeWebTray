using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace OpenCodeWebTray;

/// <summary>
/// 托盘应用主上下文：管理 opencode web 后台进程、托盘图标与交互。
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly int _port;
    private readonly string _distro;      // 目标 WSL 发行版；空 = Windows 原生模式
    private readonly string _wslUser;     // 在 WSL 发行版中以哪个用户身份运行 opencode
    private readonly string _opencodeUrl;
    private readonly string _opencodeArgs;

    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _iconOn;
    private readonly Icon _iconOff;

    private readonly ToolStripMenuItem _miOpenPage;
    private readonly ToolStripMenuItem _miStart;
    private readonly ToolStripMenuItem _miStop;
    private readonly ToolStripMenuItem _miExit;

    private Process _process;
    private DateTime _processStartTime; // opencode 进程启动时刻，用于判断是否"秒退"
    private volatile bool _isRunning;   // opencode 后台进程是否运行中
    private bool _isExiting;            // 本程序是否正在退出
    private readonly SynchronizationContext _uiContext; // UI 线程同步上下文（跨线程回调用）

    public TrayApplicationContext()
    {
        _iconOn = LoadIcon("OpenCodeWebTray.Assets.opencode.ico");
        _iconOff = LoadIcon("OpenCodeWebTray.Assets.opencode-gray.ico");

        // 从同名 INI 配置读取端口与可选的 WSL 发行版（不存在则按默认值生成）
        var cfg = TrayConfig.LoadOrCreate();
        _port = cfg.Port;
        _distro = cfg.Distro; // 已 Trim，空串 = Windows 原生模式
        _wslUser = cfg.WslUser;
        _opencodeUrl = $"http://127.0.0.1:{_port}/";
        _opencodeArgs = "web --port " + _port;

        _miOpenPage = new ToolStripMenuItem("打开网页");
        _miStart = new ToolStripMenuItem("开启");
        _miStop = new ToolStripMenuItem("关闭");
        _miExit = new ToolStripMenuItem("Exit");
        _miOpenPage.Click += (_, _) => OpenUrl(_opencodeUrl);
        _miStart.Click += (_, _) => StartOpencode();
        _miStop.Click += (_, _) => StopOpencode();
        _miExit.Click += (_, _) => ExitApp();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_miOpenPage);
        menu.Items.Add(new ToolStripSeparator());
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
        _notifyIcon.MouseDoubleClick += NotifyIcon_MouseDoubleClick;

        // 捕获 UI 线程同步上下文，供 opencode 退出回调跨线程回到 UI 线程
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

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

    private void NotifyIcon_MouseDoubleClick(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        // 单击无动作，故双击可干净触发，无需定时器去抖
        ToggleOpencode();
    }

    private void ToggleOpencode()
    {
        if (_isRunning) StopOpencode();
        else StartOpencode();
    }

    // ---------- 网页 ----------

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

    // ---------- WSL 校验 ----------

    private enum WslStatus { Ok, NotAvailable, DistroNotFound, NotWsl2 }

    /// <summary>
    /// 校验 WSL 是否可用、目标发行版是否存在且为 WSL2。
    /// 通过 `wsl.exe -l -v` 一次性获取发行版列表；失败时在 <paramref name="message"/> 中
    /// 返回面向用户的中文错误说明。
    /// </summary>
    private static WslStatus CheckWslDistro(string distro, out string message)
    {
        message = null;
        string output;
        int exitCode;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add("-v");
            // wsl.exe 默认输出 UTF-16LE，会出现 NUL 字节干扰解析；
            // 设 WSL_UTF8=1 让其输出 UTF-8（仅作用于本子进程）
            psi.EnvironmentVariables["WSL_UTF8"] = "1";
            using var p = Process.Start(psi);
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            exitCode = p.ExitCode;
        }
        catch (Exception ex)
        {
            // wsl.exe 不在 PATH（WSL 未安装）或无法启动
            message = "无法启动 wsl.exe，WSL 可能未安装或未启用：\n\n" + ex.Message +
                      "\n\n可在管理员 PowerShell 中执行 `wsl --install`，或通过" +
                      "「设置 → 应用 → 可选功能 → 更多 Windows 功能」安装" +
                      "「适用于 Linux 的 Windows 子系统」。";
            return WslStatus.NotAvailable;
        }

        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            message = "WSL 已安装但当前不可用（wsl.exe 返回码 " + exitCode + "）。\n\n" +
                      "请在终端运行 `wsl -l -v` 确认状态，或执行 `wsl --install` 完成初始化。";
            return WslStatus.NotAvailable;
        }

        // 解析输出。行格式：「* NAME STATE VERSION」，发行版名理论上可含空格，
        // 故取「末列=VERSION、倒数第二=STATE、其余合并=NAME」。
        // 表头行的 VERSION 列不是 1/2，会被下面的 version 检查自然过滤。
        bool found = false;
        bool isV2 = false;
        foreach (string raw in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            var parts = line.TrimStart('*').Trim()
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            string version = parts[parts.Length - 1];
            if (version != "1" && version != "2") continue; // 非数据行（表头 / 杂散）

            string name = string.Join(" ", parts, 0, parts.Length - 2);
            if (name.Equals(distro, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                isV2 = version == "2";
                break;
            }
        }

        if (!found)
        {
            message = $"WSL 发行版「{distro}」不存在。\n\n" +
                      "请在终端运行 `wsl -l -v` 查看已安装的发行版名称" +
                      "（注意大小写与连字符，如 Ubuntu-22.04）。";
            return WslStatus.DistroNotFound;
        }
        if (!isV2)
        {
            message = $"WSL 发行版「{distro}」不是 WSL2。\n\n" +
                      "本工具依赖 WSL2 的 localhost 端口转发，WSL1 不支持。\n" +
                      $"可执行 `wsl --set-version {distro} 2` 转换（需几分钟）。";
            return WslStatus.NotWsl2;
        }
        return WslStatus.Ok;
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
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            if (!string.IsNullOrEmpty(_distro))
            {
                // ===== WSL 模式 =====
                // 先校验 WSL / 发行版 / 版本，任一失败则弹错误并放弃启动。
                var status = CheckWslDistro(_distro, out string err);
                if (status != WslStatus.Ok)
                {
                    _process = null;
                    _isRunning = false;
                    MessageBox.Show(err, "OpenCode Web Tray",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    UpdateState();
                    return;
                }

                // Linux 下 opencode 是真实可执行文件 / 带 shebang 的脚本（无 .cmd shim 问题），
                // 用 bash -lc 启动可加载 profile 中的 PATH（如 nvm/npm 全局安装目录）。
                // --hostname 0.0.0.0 是 load-bearing：WSL2 的 localhost 端口转发只对
                // 绑定 0.0.0.0 的 listener 生效；opencode 默认绑 127.0.0.1，那样 Windows
                // 浏览器访问 http://localhost:<port> 会连不上。
                psi.FileName = "wsl.exe";
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(_distro);
                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add(_wslUser);
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add("bash");
                psi.ArgumentList.Add("-lc");
                psi.ArgumentList.Add("opencode " + _opencodeArgs + " --hostname 0.0.0.0");
                psi.EnvironmentVariables["WSL_UTF8"] = "1";
            }
            else
            {
                // ===== Windows 原生模式 =====
                // opencode 经 npm 安装时，PATH 入口是 .cmd/.ps1 脚本，
                // UseShellExecute=false 无法直接执行这些 shim；
                // 改用 cmd /c 让 Windows 经 PATHEXT 解析（CreateNoWindow 同时隐藏窗口）。
                // 此设计是 load-bearing 的，见 AGENTS.md，勿简化。
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c opencode " + _opencodeArgs;
            }

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += OnProcessExited;
            _process.Start();
            _processStartTime = DateTime.Now;
            _isRunning = true;
        }
        catch (Exception ex)
        {
            _process = null;
            _isRunning = false;
            string hint = string.IsNullOrEmpty(_distro)
                ? "请确认 opencode 已安装并在 Windows PATH 中（在终端执行 `opencode --version` 验证）。"
                : $"请确认 WSL 发行版「{_distro}」中已安装 opencode 并在其 PATH 中" +
                  "（在该发行版终端执行 `opencode --version` 验证）。";
            MessageBox.Show(
                "无法启动 opencode：\n\n" + ex.Message + "\n\n" + hint,
                "OpenCode Web Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        UpdateState();
    }

    private void OnProcessExited(object sender, EventArgs e)
    {
        // Process.Exited 在线程池线程触发，需切回 UI 线程更新界面并提示
        DateTime startedAt = _processStartTime;
        try { _process?.Dispose(); } catch { }
        _process = null;
        _isRunning = false;

        if (_isExiting) return;

        // 回到 UI 线程：更新状态 + 异常退出提示
        void OnUi()
        {
            UpdateState();
            // 启动后短时间内退出，大概率是端口冲突 / 启动失败
            double secs = (DateTime.Now - startedAt).TotalSeconds;
            if (secs < 5)
                ShowBalloon("opencode 启动失败",
                    $"端口 {_port} 可能被占用，或 opencode 启动异常。可右键「开启」重试。",
                    ToolTipIcon.Warning);
            else
                ShowBalloon("opencode 已停止",
                    "后台进程已退出。可右键「开启」重新启动。",
                    ToolTipIcon.Info);
        }

        if (_uiContext != null)
            _uiContext.Post(_ => OnUi(), null);
        else
            try { _notifyIcon.ContextMenuStrip?.BeginInvoke((Action)OnUi); } catch { }
    }

    /// <summary>
    /// 在目标 WSL 发行版内终止 opencode（按命令行精确匹配端口，避免误杀其他实例）。
    /// 这是 WSL 模式下干净停止的唯一可靠方式：直接 Kill wsl.exe 会让内部进程被
    /// wslhost.exe 接管成为孤儿，继续占用端口。
    /// </summary>
    private void KillOpencodeInsideWsl()
    {
        if (string.IsNullOrEmpty(_distro)) return;
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(_distro);
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(_wslUser);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-c");
        // 精确匹配端口的 opencode 实例；|| true 保证无匹配时也返回 0
        psi.ArgumentList.Add($"pkill -f 'opencode web --port {_port}' || true");
        psi.EnvironmentVariables["WSL_UTF8"] = "1";
        using var p = Process.Start(psi);
        p.WaitForExit(5000);
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
                if (!string.IsNullOrEmpty(_distro))
                {
                    // WSL 模式两阶段杀：
                    // ① 从 WSL 内部 pkill opencode；
                    // ② 等 wsl.exe 自然退出（opencode 死 → bash 退出 → wsl.exe 退出）；
                    // ③ 仍不死才 fallback 强杀（此时内部已空，无害）。
                    try { KillOpencodeInsideWsl(); } catch { }
                    try { _process.WaitForExit(8000); } catch { }
                }

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

    private void ShowBalloon(string title, string text, ToolTipIcon icon, int timeout = 4000)
    {
        if (_notifyIcon == null) return;
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(timeout);
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
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    // WSL 模式：同样先内部 pkill 再 fallback 强杀，避免遗留孤儿进程占端口。
                    if (!string.IsNullOrEmpty(_distro))
                    {
                        try { KillOpencodeInsideWsl(); } catch { }
                        try { _process.WaitForExit(5000); } catch { }
                    }
                    if (!_process.HasExited)
                        _process.Kill(entireProcessTree: true);
                }
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
