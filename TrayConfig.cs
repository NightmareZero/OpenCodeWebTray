using System.IO;
using System.Reflection;
using System.Text;

namespace OpenCodeWebTray;

/// <summary>
/// 读取/生成 exe 同目录下的同名 INI 配置文件（OpenCodeWebTray.ini）。
/// 承载 opencode web 监听端口，以及可选的 WSL 发行版选择与运行身份。
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

    /// <summary>
    /// 返回应使用的配置。配置文件不存在时按默认值创建一份。
    /// 存在但端口缺失/非法时回退默认值（不静默覆盖用户文件）。
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
        // distro 留空或纯空白都视为 Windows 原生模式
        distro = (distro ?? "").Trim();
        // wslUser 缺失/空白回退默认 root（仅在 WSL 模式下使用）
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
        string content =
            "; OpenCode Web Tray 配置文件\r\n" +
            "; 修改后需重启程序才能生效\r\n" +
            "\r\n" +
            "[opencode]\r\n" +
            "; opencode web 监听端口 (1-65535)\r\n" +
            "port=4096\r\n" +
            "\r\n" +
            "; 使用哪个环境中的 opencode\r\n" +
            "; 留空(默认) = 使用 Windows 原生 opencode (需 opencode 在 Windows PATH 中)\r\n" +
            "; 填 WSL 发行版名(如 Ubuntu、Ubuntu-22.04、Debian) = 在该 WSL2 发行版中启动 opencode\r\n" +
            ";   - 必须是 WSL2 (依赖其 localhost 端口转发; WSL1 不支持)\r\n" +
            ";   - 发行版需已安装,且 opencode 需在其 PATH 中 (在该发行版终端执行 opencode --version 验证)\r\n" +
            ";   - 名称需与 `wsl -l -v` 输出完全一致 (含大小写与连字符)\r\n" +
            "distro=\r\n" +
            "\r\n" +
            "; 以下配置仅当 [opencode] distro 非空 (WSL 模式) 时生效\r\n" +
            "[WSL]\r\n" +
            "; 在 WSL 发行版中以哪个用户身份运行 opencode\r\n" +
            "; 默认 root (避免文件权限问题); 也可填普通用户名 (如 ubuntu、yourname)\r\n" +
            "; 该用户必须已存在于目标发行版中\r\n" +
            "user=root\r\n";
        try
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        catch
        {
            // 目录只读等情况下忽略写入，运行期回退到默认端口
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
