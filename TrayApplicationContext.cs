using System.Reflection;
using System.Windows.Forms;

namespace OpenCodeWebTray;

/// <summary>
/// 托盘应用主上下文：多实例协调器。
/// 管理多个 OpencodeInstance，每个对应一个 [profile.*] 配置。
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly List<OpencodeInstance> _instances;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _iconOn;
    private readonly Icon _iconOff;
    private readonly SynchronizationContext _uiContext;
    private readonly OpencodeInstance _defaultInstance;
    private bool _isExiting;

    /// <summary>
    /// 每个实例对应的菜单项引用，用于就地刷新 Text/Enabled，不重建菜单。
    /// </summary>
    private sealed record InstanceMenuItems(
        ToolStripMenuItem Title,
        ToolStripMenuItem OpenPage,
        ToolStripMenuItem Toggle
    );

    private readonly Dictionary<OpencodeInstance, InstanceMenuItems> _menuMap = new();

    public TrayApplicationContext()
    {
        _iconOn = LoadIcon("OpenCodeWebTray.Assets.opencode.ico");
        _iconOff = LoadIcon("OpenCodeWebTray.Assets.opencode-gray.ico");

        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        // 加载多 profile 配置
        var loaded = TrayConfig.LoadOrCreate();

        // 为每个 profile 创建 OpencodeInstance
        _instances = new List<OpencodeInstance>(loaded.Profiles.Count);
        foreach (var profile in loaded.Profiles)
        {
            var inst = new OpencodeInstance(profile, _uiContext, (t, m, i) => ShowBalloon(t, m, i));
            inst.StateChanged += OnInstanceStateChanged;
            _instances.Add(inst);
        }

        // 解析默认实例：匹配 loaded.DefaultProfile（如 "profile.windows" 或 ""）
        // 大小写不敏感，匹配 "profile.{Name}" 或 "Name"
        if (!string.IsNullOrEmpty(loaded.DefaultProfile))
        {
            _defaultInstance = _instances.FirstOrDefault(inst =>
                string.Equals("profile." + inst.Profile.Name, loaded.DefaultProfile, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(inst.Profile.Name, loaded.DefaultProfile, StringComparison.OrdinalIgnoreCase));
        }

        // 构建菜单
        var menu = BuildMenu();

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
            Text = "OpenCode Web Tray",
        };
        _notifyIcon.MouseDoubleClick += NotifyIcon_MouseDoubleClick;

        // 启动时只起默认实例；若无默认或不满足启动条件则仅显示托盘
        if (_defaultInstance != null && _defaultInstance.CanStart)
            _defaultInstance.Start();

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

    // ---------- 菜单构建（一次性） ----------

    /// <summary>
    /// 构建右键菜单。每个 profile 一块，包含标题行（灰显）+ 打开网页 + 开启/关闭（单按钮，按状态切换），
    /// 块间以分隔符隔开；最后是 Exit。
    /// 菜单项引用存入 _menuMap，供 OnInstanceStateChanged 就地刷新。
    /// </summary>
    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        _menuMap.Clear();

        foreach (var inst in _instances)
        {
            // 标题行：灰显，显示名称 + 状态标签
            var title = new ToolStripMenuItem($"{inst.Profile.Name}  {inst.StateLabel}")
            {
                Enabled = false
            };

            var openPage = new ToolStripMenuItem("打开网页") { Enabled = inst.IsRunning };
            openPage.Click += (_, _) => inst.OpenPage();

            // 开启/关闭合并为单个按钮：按当前状态显示文案并切换动作
            //  - 运行中 → "关闭"，点击 Stop
            //  - 已停止且可启动 → "开启"，点击 Start
            //  - 端口冲突/配置无效 → "开启" 但禁用（标题已显示原因）
            var toggle = new ToolStripMenuItem(inst.IsRunning ? "关闭" : "开启")
            {
                Enabled = inst.CanStart
            };
            toggle.Click += (_, _) =>
            {
                if (inst.IsRunning) inst.Stop();
                else inst.Start();
            };

            menu.Items.Add(title);
            menu.Items.Add(openPage);
            menu.Items.Add(toggle);
            menu.Items.Add(new ToolStripSeparator());

            _menuMap[inst] = new InstanceMenuItems(title, openPage, toggle);
        }

        // Exit（始终在最末）
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(exit);

        return menu;
    }

    // ---------- 鼠标交互 ----------

    private void NotifyIcon_MouseDoubleClick(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        // 单击无动作，故双击可干净触发，无需定时器去抖

        if (_defaultInstance != null)
        {
            if (_defaultInstance.IsRunning)
                _defaultInstance.Stop();
            else
                _defaultInstance.Start();
        }
        else
        {
            ShowBalloon(
                "未设置默认 profile",
                "在配置文件 [tray].default 设置一个 profile 标识以启用双击切换。",
                ToolTipIcon.Info);
        }
    }

    // ---------- 状态刷新（就地更新，不重建菜单） ----------

    private void OnInstanceStateChanged(object sender, EventArgs e)
    {
        if (_isExiting) return;

        if (sender is OpencodeInstance inst && _menuMap.TryGetValue(inst, out var items))
        {
            items.Title.Text = $"{inst.Profile.Name}  {inst.StateLabel}";
            items.OpenPage.Enabled = inst.IsRunning;
            items.Toggle.Text = inst.IsRunning ? "关闭" : "开启";
            items.Toggle.Enabled = inst.CanStart;
        }
        UpdateState();
    }

    private void UpdateState()
    {
        if (_notifyIcon == null) return;

        int runningCount = _instances.Count(i => i.IsRunning);
        _notifyIcon.Icon = runningCount > 0 ? _iconOn : _iconOff;

        if (_instances.Count == 0)
            _notifyIcon.Text = "OpenCode Web Tray (无配置)";
        else if (runningCount > 0)
            _notifyIcon.Text = $"OpenCode Web Tray ({runningCount} 个运行中)";
        else
            _notifyIcon.Text = "OpenCode Web Tray (已停止)";
    }

    // ---------- 退出 ----------

    private void ExitApp()
    {
        _isExiting = true;
        foreach (var inst in _instances)
            inst.Stop();

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _iconOn.Dispose();
        _iconOff.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _isExiting = true;
            foreach (var inst in _instances)
                inst.Dispose();
            _notifyIcon?.Dispose();
            _iconOn?.Dispose();
            _iconOff?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------- 气球提示（作为回调注入 OpencodeInstance） ----------

    private void ShowBalloon(string title, string text, ToolTipIcon icon, int timeout = 4000)
    {
        if (_notifyIcon == null) return;
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(timeout);
    }
}
