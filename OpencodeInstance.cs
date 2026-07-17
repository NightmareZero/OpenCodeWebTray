using System.Diagnostics;
using System.Text;

namespace OpenCodeWebTray;

/// <summary>
/// 单实例 opencode web 进程管理。
/// 每 Profile 对应一个实例，协调器持有 List&lt;OpencodeInstance&gt;。
/// </summary>
internal sealed class OpencodeInstance : IDisposable
{
    public Profile Profile { get; }
    public bool IsRunning => _isRunning;
    public bool CanStart => Profile.Port > 0 && !Profile.PortConflicted;
    public string StateLabel
    {
        get
        {
            if (Profile.PortConflicted) return "⚠端口冲突";
            if (Profile.Port <= 0) return "⚠配置无效";
            if (_isRunning) return "●运行中";
            return "○已停止";
        }
    }

    public event EventHandler StateChanged;

    private Process _process;
    private DateTime _processStartTime;
    private volatile bool _isRunning;
    private bool _isExiting;
    private readonly SynchronizationContext _uiContext;
    private readonly string _opencodeUrl;
    private readonly string _opencodeArgs;
    private readonly Action<string, string, ToolTipIcon> _showBalloon;

    /// <param name="showBalloon">可选气球提示回调 (title, text, icon)。
    /// 协调器注入: 进程异常退出时由实例自行判断原因并提示, 而显示动作委托给持有 NotifyIcon 的协调器。</param>
    public OpencodeInstance(Profile profile, SynchronizationContext uiContext,
        Action<string, string, ToolTipIcon> showBalloon = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _uiContext = uiContext ?? throw new ArgumentNullException(nameof(uiContext));
        _showBalloon = showBalloon;
        _opencodeUrl = $"http://127.0.0.1:{Profile.Port}/";
        _opencodeArgs = "web --port " + Profile.Port;
    }

    // ---------- 启动 ----------

    public void Start()
    {
        if (_isExiting) return;
        if (!CanStart)
        {
            OnStateChanged();
            return;
        }
        if (_isRunning || _process != null)
        {
            OnStateChanged();
            return;
        }

        // 启动前端口探测：只读检查端口是否已被占用，绝不尝试识别或杀掉占用进程
        if (IsPortInUse(Profile.Port))
        {
            MessageBox.Show(
                $"端口 {Profile.Port} 已被占用…\n\n可能是其他程序或已运行的 opencode 实例。",
                "OpenCode Web Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
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

            if (!string.IsNullOrEmpty(Profile.Distro))
            {
                // ===== WSL 模式 =====
                // 先校验 WSL / 发行版 / 版本，任一失败则弹错误并放弃启动。
                var status = CheckWslDistro(Profile.Distro, out string err);
                if (status != WslStatus.Ok)
                {
                    _process = null;
                    _isRunning = false;
                    MessageBox.Show(err, "OpenCode Web Tray",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    OnStateChanged();
                    return;
                }

                // Linux 下 opencode 是真实可执行文件 / 带 shebang 的脚本（无 .cmd shim 问题），
                // 用 bash -lc 启动可加载 profile 中的 PATH（如 nvm/npm 全局安装目录）。
                // --hostname 0.0.0.0 是 load-bearing：WSL2 的 localhost 端口转发只对
                // 绑定 0.0.0.0 的 listener 生效；opencode 默认绑 127.0.0.1，那样 Windows
                // 浏览器访问 http://localhost:<port> 会连不上。
                psi.FileName = "wsl.exe";
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(Profile.Distro);
                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add(Profile.WslUser);
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
            string hint = string.IsNullOrEmpty(Profile.Distro)
                ? "请确认 opencode 已安装并在 Windows PATH 中（在终端执行 opencode --version 验证）。"
                : $"请确认 WSL 发行版「{Profile.Distro}」中已安装 opencode 并在其 PATH 中" +
                  "（在该发行版终端执行 opencode --version 验证）。";
            MessageBox.Show(
                "无法启动 opencode：\n\n" + ex.Message + "\n\n" + hint,
                "OpenCode Web Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        OnStateChanged();
    }

    // ---------- 停止 ----------

    public void Stop()
    {
        if (_process == null || !_isRunning)
        {
            OnStateChanged();
            return;
        }

        try
        {
            _process.Exited -= OnProcessExited;
            if (!_process.HasExited)
            {
                if (!string.IsNullOrEmpty(Profile.Distro))
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

        OnStateChanged();
    }

    // ---------- 打开网页 ----------

    public void OpenPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_opencodeUrl) { UseShellExecute = true });
        }
        catch
        {
            // 忽略打开失败
        }
    }

    // ---------- 释放 ----------

    public void Dispose()
    {
        _isExiting = true;
        try
        {
            if (_process != null && !_process.HasExited)
            {
                // WSL 模式：同样先内部 pkill 再 fallback 强杀，避免遗留孤儿进程占端口。
                if (!string.IsNullOrEmpty(Profile.Distro))
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
        _process = null;
        _isRunning = false;
    }

    // ---------- 端口探测（只读，绝不动占用进程） ----------

    /// <summary>
    /// 探测指定端口是否处于 Listen 状态（只读检查，不尝试识别/杀掉占用进程）。
    /// 返回 true 表示端口已被占用；探测失败时返回 false（不阻塞启动，由秒退 balloon 兜底）。
    /// </summary>
    private static bool IsPortInUse(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -Command \"Get-NetTCPConnection -LocalPort " +
                    port + " -State Listen -ErrorAction SilentlyContinue\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            // 探测失败不阻塞启动，由 5 秒秒退 balloon 兜底
            return false;
        }
    }

    // ---------- WSL 校验（逐字复制自 TrayApplicationContext） ----------

    private enum WslStatus { Ok, NotAvailable, DistroNotFound, NotWsl2 }

    /// <summary>
    /// 校验 WSL 是否可用、目标发行版是否存在且为 WSL2。
    /// 通过 wsl.exe -l -v 一次性获取发行版列表；失败时在 <paramref name="message"/> 中
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
                      "\n\n可在管理员 PowerShell 中执行 wsl --install，或通过" +
                      "「设置 → 应用 → 可选功能 → 更多 Windows 功能」安装" +
                      "「适用于 Linux 的 Windows 子系统」。";
            return WslStatus.NotAvailable;
        }

        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            message = "WSL 已安装但当前不可用（wsl.exe 返回码 " + exitCode + "）。\n\n" +
                      "请在终端运行 wsl -l -v 确认状态，或执行 wsl --install 完成初始化。";
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
                      "请在终端运行 wsl -l -v 查看已安装的发行版名称" +
                      "（注意大小写与连字符，如 Ubuntu-22.04）。";
            return WslStatus.DistroNotFound;
        }
        if (!isV2)
        {
            message = $"WSL 发行版「{distro}」不是 WSL2。\n\n" +
                      "本工具依赖 WSL2 的 localhost 端口转发，WSL1 不支持。\n" +
                      $"可执行 wsl --set-version {distro} 2 转换（需几分钟）。";
            return WslStatus.NotWsl2;
        }
        return WslStatus.Ok;
    }

    // ---------- WSL 两阶段停止（逐字复制自 TrayApplicationContext） ----------

    /// <summary>
    /// 在目标 WSL 发行版内终止 opencode（按命令行精确匹配端口，避免误杀其他实例）。
    /// 这是 WSL 模式下干净停止的唯一可靠方式：直接 Kill wsl.exe 会让内部进程被
    /// wslhost.exe 接管成为孤儿，继续占用端口。
    /// </summary>
    private void KillOpencodeInsideWsl()
    {
        if (string.IsNullOrEmpty(Profile.Distro)) return;
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(Profile.Distro);
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(Profile.WslUser);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-c");
        // 精确匹配端口的 opencode 实例；|| true 保证无匹配时也返回 0
        psi.ArgumentList.Add($"pkill -f 'opencode web --port {Profile.Port}' || true");
        psi.EnvironmentVariables["WSL_UTF8"] = "1";
        using var p = Process.Start(psi);
        p.WaitForExit(5000);
    }

    // ---------- 进程退出回调（逐字复制自 TrayApplicationContext） ----------

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
            OnStateChanged();
            // 启动后短时间内退出，大概率是端口冲突 / 启动失败
            double secs = (DateTime.Now - startedAt).TotalSeconds;
            if (_showBalloon != null)
            {
                if (secs < 5)
                    _showBalloon("opencode 启动失败",
                        $"端口 {Profile.Port} 可能被占用，或 opencode 启动异常。可右键「开启」重试。",
                        ToolTipIcon.Warning);
                else
                    _showBalloon("opencode 已停止",
                        "后台进程已退出。可右键「开启」重新启动。",
                        ToolTipIcon.Info);
            }
        }

        if (_uiContext != null)
            _uiContext.Post(_ => OnUi(), null);
        else
            OnUi();
    }

    // ---------- 状态变更通知 ----------

    /// <summary>
    /// 触发 StateChanged 事件。总通过 _uiContext.Post 保证订阅者在 UI 线程执行。
    /// </summary>
    private void OnStateChanged()
    {
        if (_uiContext != null)
            _uiContext.Post(_ => StateChanged?.Invoke(this, EventArgs.Empty), null);
        else
            StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
