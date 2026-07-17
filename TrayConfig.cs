using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace OpenCodeWebTray;

/// <summary>
/// 单个 opencode 实例的配置。
/// Name = [profile.XXX] 的 XXX 部分(已 Trim), 用作显示名。
/// Port &lt;=0 表示配置非法(缺失/越界), 该 profile 不可启动。
/// Distro 为空表示 Windows 原生；非空表示 WSL2 发行版名。
/// WslUser 仅在 WSL 模式生效；空/缺失 → "root"。
/// PortConflicted = true 表示与其他 profile 端口相同。
/// </summary>
internal sealed record Profile(
    string Name,
    int Port,
    string Distro,
    string WslUser,
    bool PortConflicted
);

internal static class TrayConfig
{
    /// <summary>
    /// 加载结果。DefaultProfile 保留 [tray].default 的原值(如 "profile.windows" 或 "")。
    /// </summary>
    public sealed record LoadedConfig(string DefaultProfile, List<Profile> Profiles);

    private const int DefaultPort = 4096;
    private const string DefaultWslUser = "root";

    // ──────────────── 模板块 ────────────────

    private static readonly string[] FileHeaderLines =
    {
        "; OpenCode Web Tray 配置文件",
        "; 修改后需重启程序才能生效",
        "",
    };

    private static readonly string[] TrayDefaultComment =
    {
        "; 启动时自动开启哪个 profile (填 profile 标识, 如 profile.windows)",
        "; 留空 = 启动时不自动开启任何实例, 仅显示托盘",
    };

    private static readonly string[] ProfileSeparatorComment =
    {
        "; ===== 每个 [profile.名称] 定义一套独立配置 =====",
        "; 名称建议用英文, 不可重复; 各 profile 的 port 不可冲突",
        "; 新增 profile: 复制下面整块、改 [profile.xxx] 名和 port 即可, 无需抄注释",
    };

    private static readonly string[] ProfileWindowsComment =
    {
        "; opencode web 监听端口 (1-65535)",
        "; 使用哪个环境中的 opencode (留空=Windows原生 / 填发行版名=WSL2)",
        "; 仅 WSL 模式生效: 运行身份 (默认 root)",
    };

    // ──────────────── LoadOrCreate ────────────────

    /// <summary>
    /// 读取/生成 exe 同目录下的同名 INI 配置文件（OpenCodeWebTray.ini）。
    /// 返回 (defaultProfile 原值, profiles 列表)。文件不存在时按模板生成并返回默认。
    /// </summary>
    public static LoadedConfig LoadOrCreate()
    {
        string path = ResolveConfigPath();
        if (!File.Exists(path))
        {
            WriteDefault(path);
            return DefaultLoadedConfig();
        }

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return DefaultLoadedConfig(); }

        // 格式探测
        bool hasProfileSection = HasProfileSection(lines);
        bool hasOldSection = HasOldFormatSection(lines);

        if (!hasProfileSection && hasOldSection)
        {
            // v1.0.2 及更早旧格式 → 迁移
            return MigrateOldConfig(path, lines);
        }

        // 新格式：解析 profile
        var result = ParseConfig(lines);
        DetectPortConflicts(result.Profiles);
        EnsureCompleteConfig(path);
        return result;
    }

    /// <summary>
    /// 配置文件路径：与 exe 同名、同目录，扩展名替换为 .ini。
    /// </summary>
    public static string ResolveConfigPath()
    {
        string dir;
        string name;

        string exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            dir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
            name = Path.GetFileNameWithoutExtension(exePath);
        }
        else
        {
            dir = AppContext.BaseDirectory;
            name = Assembly.GetEntryAssembly()?.GetName().Name;
        }

        if (string.IsNullOrEmpty(name)) name = "OpenCodeWebTray";
        return Path.Combine(dir, name + ".ini");
    }

    // ──────────────── 写默认配置 ────────────────

    private static void WriteDefault(string path)
    {
        var lines = new List<string>();
        lines.AddRange(FileHeaderLines);
        lines.Add("[tray]");
        lines.AddRange(TrayDefaultComment);
        lines.Add("default=profile.windows");
        lines.Add("");
        lines.AddRange(ProfileSeparatorComment);
        lines.Add("");
        lines.Add("[profile.windows]");
        lines.Add("; opencode web 监听端口 (1-65535)");
        lines.Add("port=4096");
        lines.Add("; 使用哪个环境中的 opencode (留空=Windows原生 / 填发行版名=WSL2)");
        lines.Add("distro=");
        lines.Add("; 仅 WSL 模式生效: 运行身份 (默认 root)");
        lines.Add("user=root");
        WriteAllLinesSafe(path, lines);
    }

    private static LoadedConfig DefaultLoadedConfig()
    {
        return new LoadedConfig("profile.windows", new List<Profile>
        {
            new Profile("windows", DefaultPort, "", DefaultWslUser, false)
        });
    }

    // ──────────────── 格式探测 ────────────────

    /// <summary>是否存在 [profile.*] 节。</summary>
    private static bool HasProfileSection(string[] lines)
    {
        foreach (string raw in lines)
        {
            string t = raw.Trim();
            if (t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']')
            {
                string sec = t[1..^1].Trim();
                if (sec.StartsWith("profile.", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>是否存在旧格式 [opencode] 或 [WSL] 节。</summary>
    private static bool HasOldFormatSection(string[] lines)
    {
        foreach (string raw in lines)
        {
            string t = raw.Trim();
            if (t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']')
            {
                string sec = t[1..^1].Trim();
                if (string.Equals(sec, "opencode", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(sec, "wsl", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    // ──────────────── 旧格式迁移 ────────────────

    /// <summary>从旧格式 INI 迁移为新格式。失败时静默回退到内存合成值。</summary>
    private static LoadedConfig MigrateOldConfig(string path, string[] lines)
    {
        // 解析旧格式
        int port = -1;
        string distro = null;
        string wslUser = null;
        TryReadOldConfig(lines, out int oldPort, out string oldDistro, out string oldUser);
        if (oldPort > 0) port = oldPort;
        if (oldDistro != null) distro = oldDistro.Trim();
        if (!string.IsNullOrWhiteSpace(oldUser)) wslUser = oldUser.Trim();

        int finalPort = port > 0 ? port : DefaultPort;
        string finalDistro = distro ?? "";
        string finalUser = string.IsNullOrWhiteSpace(wslUser) ? DefaultWslUser : wslUser.Trim();

        // 构建新内容
        var newLines = new List<string>();
        newLines.AddRange(FileHeaderLines);
        newLines.Add("[tray]");
        newLines.AddRange(TrayDefaultComment);
        newLines.Add("default=profile.windows");
        newLines.Add("");
        newLines.AddRange(ProfileSeparatorComment);
        newLines.Add("");
        newLines.Add("[profile.windows]");
        newLines.AddRange(ProfileWindowsComment);
        newLines.Add($"port={finalPort}");
        newLines.Add($"distro={finalDistro}");
        newLines.Add($"user={finalUser}");

        // 原子写 + 备份 .bak
        MigrateWriteSafe(path, newLines);

        return new LoadedConfig("profile.windows", new List<Profile>
        {
            new Profile("windows", finalPort, finalDistro, finalUser, false)
        });
    }

    /// <summary>
    /// 原子迁移：备份(.bak) → 写(.tmp) → Move 替换。
    /// 任一步失败则尝试从 .bak 还原；还原失败也忽略。目录只读时仅做内存迁移。
    /// </summary>
    private static void MigrateWriteSafe(string path, List<string> newLines)
    {
        string bakPath = path + ".bak";
        string tmpPath = path + ".tmp";
        try
        {
            // 1. 备份旧文件
            File.Copy(path, bakPath, overwrite: true);
            // 2. 写新内容到临时文件
            WriteAllLinesSafe(tmpPath, newLines);
            // 3. 原子替换
            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            // 尝试从 .bak 还原
            try
            {
                if (File.Exists(bakPath))
                    File.Copy(bakPath, path, overwrite: true);
            }
            catch
            {
                // 还原失败也忽略
            }
            // 清理 .tmp
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
        }
    }

    /// <summary>解析旧格式 [opencode]/[WSL] 节的值（复用原 TryReadConfig 逻辑）。</summary>
    private static void TryReadOldConfig(string[] lines, out int port, out string distro, out string wslUser)
    {
        port = -1;
        distro = null;
        wslUser = null;
        string currentSection = null;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == ';' || line[0] == '#') continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                currentSection = line.Substring(1, line.Length - 2)
                    .Trim().ToLowerInvariant();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string val = line.Substring(eq + 1).Trim();

            if (currentSection == "opencode")
            {
                if (key == "port")
                {
                    if (int.TryParse(val, out int p) && p >= 1 && p <= 65535)
                        port = p;
                }
                else if (key == "distro")
                {
                    distro = val;
                }
            }
            else if (currentSection == "wsl")
            {
                if (key == "user")
                {
                    wslUser = val;
                }
            }
        }
    }

    // ──────────────── 新格式解析 ────────────────

    /// <summary>
    /// 解析新格式 INI，返回 LoadedConfig。
    /// 节名大小写不敏感。[tray] 读 default 原值。
    /// [profile.XXX] 解析出 Profile。
    /// 同一 Name 重复出现时：后者 keys 合并覆盖前者（标准 INI 行为）。
    /// </summary>
    private static LoadedConfig ParseConfig(string[] lines)
    {
        string defaultProfile = "";
        var profiles = new List<Profile>();
        string curSection = null;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == ';' || line[0] == '#') continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                curSection = line[1..^1].Trim();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();

            if (string.Equals(curSection, "tray", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(key, "default", StringComparison.OrdinalIgnoreCase))
                    defaultProfile = val;
            }
            else if (curSection != null &&
                     curSection.StartsWith("profile.", StringComparison.OrdinalIgnoreCase))
            {
                string profileName = curSection["profile.".Length..].Trim();
                if (string.IsNullOrEmpty(profileName)) continue;

                // 查找已有 profile（同名合并）
                int idx = profiles.FindIndex(p =>
                    string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
                if (idx < 0)
                {
                    profiles.Add(new Profile(profileName, 0, "", DefaultWslUser, false));
                    idx = profiles.Count - 1;
                }

                var cur = profiles[idx];
                if (string.Equals(key, "port", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(val, out int p) && p >= 1 && p <= 65535)
                        cur = cur with { Port = p };
                    // 非法 port → Port 保持 0（标记不可启动）
                }
                else if (string.Equals(key, "distro", StringComparison.OrdinalIgnoreCase))
                {
                    cur = cur with { Distro = val };
                }
                else if (string.Equals(key, "user", StringComparison.OrdinalIgnoreCase))
                {
                    cur = cur with { WslUser = string.IsNullOrWhiteSpace(val) ? DefaultWslUser : val.Trim() };
                }
                profiles[idx] = cur;
            }
        }

        return new LoadedConfig(defaultProfile, profiles);
    }

    // ──────────────── 端口冲突检测 ────────────────

    /// <summary>
    /// Port>0 且端口相同的多个 profile 全部 PortConflicted=true。
    /// Port&lt;=0 的非法 profile 不参与冲突判定。
    /// </summary>
    private static void DetectPortConflicts(List<Profile> profiles)
    {
        for (int i = 0; i < profiles.Count; i++)
            profiles[i] = profiles[i] with { PortConflicted = false };

        var portGroups = profiles
            .Select((p, i) => (Profile: p, Index: i))
            .Where(x => x.Profile.Port > 0)
            .GroupBy(x => x.Profile.Port)
            .Where(g => g.Count() > 1);

        foreach (var group in portGroups)
        {
            foreach (var (profile, index) in group)
                profiles[index] = profile with { PortConflicted = true };
        }
    }

    // ──────────────── 补全 ────────────────

    /// <summary>
    /// 仅确保文件头注释 + [tray] 节 + default= key 存在；缺则补（带注释）。
    /// 绝不修改任何 [profile.*] 节的内容。
    /// </summary>
    private static void EnsureCompleteConfig(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return; }

        // ── 扫描 ──
        int firstSectionIdx = -1;
        int trayStart = -1;
        int trayEnd = -1;
        bool trayHasDefault = false;
        string curSec = null;

        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            bool isSectionHeader = t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']';

            if (isSectionHeader)
            {
                string secName = t[1..^1].Trim();
                if (firstSectionIdx == -1)
                    firstSectionIdx = i;
                if (string.Equals(secName, "tray", StringComparison.OrdinalIgnoreCase))
                {
                    trayStart = i;
                    trayEnd = i;
                    curSec = "tray";
                }
                else
                {
                    if (curSec != null && string.Equals(curSec, "tray", StringComparison.OrdinalIgnoreCase))
                        trayEnd = i - 1; // 前一行是 [tray] 节末尾
                    curSec = null;
                }
            }
            else if (curSec != null && string.Equals(curSec, "tray", StringComparison.OrdinalIgnoreCase))
            {
                trayEnd = i;
                int eq = t.IndexOf('=');
                if (eq > 0 && string.Equals(t[..eq].Trim(), "default", StringComparison.OrdinalIgnoreCase))
                    trayHasDefault = true;
            }
        }

        bool needsTray = trayStart == -1;
        bool needsDefault = trayStart != -1 && !trayHasDefault;

        if (!needsTray && !needsDefault)
            return;

        // ── Case 1: [tray] 完全缺失 ──
        if (needsTray)
        {
            // 收集 [tray] 节之外的所有行（保留 profile 节及其他内容）
            var preserved = new List<string>();
            bool inTray = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                bool isSectionHeader = t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']';

                if (isSectionHeader)
                {
                    string secName = t[1..^1].Trim();
                    if (string.Equals(secName, "tray", StringComparison.OrdinalIgnoreCase))
                    { inTray = true; continue; }
                    inTray = false;
                }
                else if (inTray)
                {
                    continue;
                }

                // 跳过文件头（第一个节之前的内容——用模板替换）
                if (firstSectionIdx >= 0 && i < firstSectionIdx)
                    continue;

                preserved.Add(lines[i]);
            }

            var result = new List<string>(lines.Length + 8);
            result.AddRange(FileHeaderLines);
            result.Add("[tray]");
            result.AddRange(TrayDefaultComment);
            result.Add("default=profile.windows");
            if (preserved.Count > 0)
            {
                result.Add("");
                result.AddRange(preserved);
            }
            WriteAllLinesSafe(path, result);
            return;
        }

        // ── Case 2: [tray] 存在但缺 default= key ──
        if (needsDefault)
        {
            var result = new List<string>(lines.Length + 4);
            for (int i = 0; i < lines.Length; i++)
            {
                result.Add(lines[i]);
                if (i == trayStart)
                {
                    // 在 [tray] 节头后立即插入 default= 及注释
                    result.AddRange(TrayDefaultComment);
                    result.Add("default=profile.windows");
                }
            }
            WriteAllLinesSafe(path, result);
        }
    }

    // ──────────────── 工具方法 ────────────────

    private static void WriteAllLinesSafe(string path, List<string> lines)
    {
        try { File.WriteAllLines(path, lines, new UTF8Encoding(false)); }
        catch
        {
            // 目录只读等情况下忽略写入，运行期回退到内存默认值
        }
    }
}
