using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace OpenCodeWebTray;

/// <summary>
/// 读取/生成 exe 同目录下的同名 INI 配置文件（OpenCodeWebTray.ini）。
/// 承载 opencode web 监听端口，以及可选的 WSL 发行版选择与运行身份。
/// 文件不存在时按默认值生成；已存在但缺少新配置项时自动补全（连同注释）。
/// </summary>
internal static class TrayConfig
{
    private const int DefaultPort = 4096;
    private const string Section = "opencode";
    private const string SectionWsl = "wsl";
    private const string DefaultWslUser = "root";

    /// <summary>
    /// 读取到的配置。
    /// <see cref="Distro"/> 为空表示使用 Windows 原生 opencode；
    /// 非空表示在指定 WSL2 发行版中启动 opencode。
    /// <see cref="WslUser"/> 仅在 WSL 模式下生效，指定运行 opencode 的用户。
    /// </summary>
    public sealed record Config(int Port, string Distro, string WslUser);

    // ---- 配置项的「注释 + 默认行」块，WriteDefault 与 EnsureCompleteConfig 共用，
    //      保证生成与补全的注释文本完全一致 ----

    private static readonly string[] FileHeaderLines =
    {
        "; OpenCode Web Tray 配置文件",
        "; 修改后需重启程序才能生效",
        "",
    };

    private static readonly string[] PortBlock =
    {
        "; opencode web 监听端口 (1-65535)",
        "port=4096",
    };

    private static readonly string[] DistroBlock =
    {
        "",
        "; 使用哪个环境中的 opencode",
        "; 留空(默认) = 使用 Windows 原生 opencode (需 opencode 在 Windows PATH 中)",
        "; 填 WSL 发行版名(如 Ubuntu、Ubuntu-22.04、Debian) = 在该 WSL2 发行版中启动 opencode",
        ";   - 必须是 WSL2 (依赖其 localhost 端口转发; WSL1 不支持)",
        ";   - 发行版需已安装,且 opencode 需在其 PATH 中 (在该发行版终端执行 opencode --version 验证)",
        ";   - 名称需与 `wsl -l -v` 输出完全一致 (含大小写与连字符)",
        "distro=",
    };

    // [WSL] 节的 user 项注释 + 默认行（不含节头）
    private static readonly string[] WslUserBlock =
    {
        "; 在 WSL 发行版中以哪个用户身份运行 opencode",
        "; 默认 root (避免文件权限问题); 也可填普通用户名 (如 ubuntu、yourname)",
        "; 该用户必须已存在于目标发行版中",
        "user=root",
    };

    // [WSL] 节头 + 引导注释（前导空行用于与上一节分隔）
    private static readonly string[] WslSectionHeaderLines =
    {
        "",
        "; 以下配置仅当 [opencode] distro 非空 (WSL 模式) 时生效",
        "[WSL]",
    };

    /// <summary>
    /// 返回应使用的配置。配置文件不存在时按默认值创建一份。
    /// 存在但端口缺失/非法时回退默认值（不静默覆盖用户文件）。
    /// 存在但缺少新配置项时，自动把缺失项连同注释补写进去（不改动用户已有的行与值）。
    /// </summary>
    public static Config LoadOrCreate()
    {
        string path = ResolveConfigPath();
        if (!File.Exists(path))
        {
            WriteDefault(path);
            return new Config(DefaultPort, "", DefaultWslUser);
        }

        TryReadConfig(path, out int port, out string distro, out string wslUser);
        // 补全缺失项（连同注释），方便用户发现可配置项；不改动用户已有的行与值
        EnsureCompleteConfig(path);
        distro = (distro ?? "").Trim();
        wslUser = string.IsNullOrWhiteSpace(wslUser) ? DefaultWslUser : wslUser.Trim();
        return new Config(port > 0 ? port : DefaultPort, distro, wslUser);
    }

    /// <summary>
    /// 配置文件路径：与 exe 同名、同目录，扩展名替换为 .ini。
    /// </summary>
    public static string ResolveConfigPath()
    {
        // Environment.ProcessPath 在单文件发布（self-contained/framework-dependent）下
        // 仍返回 bundle 后的 exe 路径。取不到时用 BaseDirectory + 入口程序集名兜底
        //（单文件下 Assembly.Location 为空，故不依赖它）。
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

    private static void WriteDefault(string path)
    {
        var lines = new List<string>();
        lines.AddRange(FileHeaderLines);
        lines.Add("[opencode]");
        lines.AddRange(PortBlock);
        lines.AddRange(DistroBlock);
        lines.AddRange(WslSectionHeaderLines);
        lines.AddRange(WslUserBlock);
        WriteAllLinesSafe(path, lines);
    }

    /// <summary>
    /// 检查已存在的配置文件，把缺失的配置项连同注释补写进去。
    /// 保留用户已有的所有行、值与自定义注释；只在对应节的末尾追加缺失项，
    /// 整节缺失时追加到文件末尾。一切齐全时不写入。
    /// </summary>
    private static void EnsureCompleteConfig(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return; }

        // 扫描：记录每个 section 已有的 key 集合，以及 section 块的最后一行索引
        //（section 块 = 从 [header] 到下一个 [header] 之前，或文件末尾）
        var sectionKeys = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var sectionEndLine = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string cur = null;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']')
            {
                cur = t.Substring(1, t.Length - 2).Trim();
                if (!sectionKeys.ContainsKey(cur))
                    sectionKeys[cur] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                sectionEndLine[cur] = i;
            }
            else if (cur != null)
            {
                sectionEndLine[cur] = i; // 注释/空行也是 section 内容，用于定位插入点
                if (t.Length > 0 && (t[0] == ';' || t[0] == '#'))
                    continue; // 跳过注释行，不作为 key 计入（与 TryReadConfig 一致）
                int eq = t.IndexOf('=');
                if (eq > 0)
                    sectionKeys[cur].Add(t.Substring(0, eq).Trim());
            }
        }

        bool hasOpencode = sectionKeys.ContainsKey(Section);
        bool hasWsl = sectionKeys.ContainsKey(SectionWsl);

        // 判定缺失：节存在但缺 key，或整个节缺失
        bool missingPort = hasOpencode && !sectionKeys[Section].Contains("port");
        bool missingDistro = hasOpencode && !sectionKeys[Section].Contains("distro");
        bool missingUser = hasWsl && !sectionKeys[SectionWsl].Contains("user");
        bool missingWslSection = !hasWsl;
        bool missingOpencodeSection = !hasOpencode;

        if (!missingPort && !missingDistro && !missingUser
            && !missingWslSection && !missingOpencodeSection)
            return; // 齐全，无需写

        // 各节末尾待追加的内容（保持 port 在 distro 前、注释带全）
        var opencodeAppend = new List<string>();
        if (missingPort) opencodeAppend.AddRange(PortBlock);
        if (missingDistro) opencodeAppend.AddRange(DistroBlock);

        var wslAppend = new List<string>();
        if (missingUser) wslAppend.AddRange(WslUserBlock);

        // 重建：逐行复制原文件，在对应 section 末尾插入缺失块
        var result = new List<string>(lines.Length + 16);
        int opencodeEnd = hasOpencode ? sectionEndLine[Section] : -1;
        int wslEnd = hasWsl ? sectionEndLine[SectionWsl] : -1;

        for (int i = 0; i < lines.Length; i++)
        {
            result.Add(lines[i]);
            if (i == opencodeEnd && opencodeAppend.Count > 0)
                result.AddRange(opencodeAppend);
            if (i == wslEnd && wslAppend.Count > 0)
                result.AddRange(wslAppend);
        }

        // 整节缺失：追加到文件末尾
        if (missingOpencodeSection)
        {
            if (result.Count > 0 && result[result.Count - 1].Trim().Length > 0)
                result.Add("");
            result.Add("[opencode]");
            result.AddRange(PortBlock);
            result.AddRange(DistroBlock);
        }
        if (missingWslSection)
        {
            result.AddRange(WslSectionHeaderLines); // 自带前导空行
            result.AddRange(WslUserBlock);
        }

        WriteAllLinesSafe(path, result);
    }

    private static void WriteAllLinesSafe(string path, List<string> lines)
    {
        try { File.WriteAllLines(path, lines, new UTF8Encoding(false)); }
        catch
        {
            // 目录只读等情况下忽略写入，运行期回退到内存默认值
        }
    }

    /// <summary>
    /// 单次遍历 INI，同时解析 port、distro（[opencode] 节）与 wsl user（[WSL] 节）。
    /// </summary>
    private static void TryReadConfig(string path, out int port, out string distro, out string wslUser)
    {
        port = -1;
        distro = null;
        wslUser = null;
        try
        {
            string currentSection = null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#') continue; // 注释

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

                if (currentSection == Section)
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
                else if (currentSection == SectionWsl)
                {
                    if (key == "user")
                    {
                        wslUser = val;
                    }
                }
            }
        }
        catch
        {
            // 读取/解析失败，回退默认（port=-1，distro/wslUser=null）
        }
    }
}
