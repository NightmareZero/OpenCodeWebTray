using System.IO;
using System.Reflection;
using System.Text;

namespace OpenCodeWebTray;

/// <summary>
/// 读取/生成 exe 同目录下的同名 INI 配置文件（OpenCodeWebTray.ini）。
/// 当前仅承载 opencode web 监听端口。
/// </summary>
internal static class TrayConfig
{
    private const int DefaultPort = 4096;
    private const string Section = "opencode";

    /// <summary>
    /// 返回应使用的端口。配置文件不存在时按默认值创建一份。
    /// 存在但端口缺失/非法时回退默认值（不静默覆盖用户文件）。
    /// </summary>
    public static int LoadOrCreate()
    {
        string path = ResolveConfigPath();
        if (!File.Exists(path))
        {
            WriteDefault(path);
            return DefaultPort;
        }

        int port = TryReadPort(path);
        return port > 0 ? port : DefaultPort;
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
            "port=4096\r\n";
        try
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        catch
        {
            // 目录只读等情况下忽略写入，运行期回退到默认端口
        }
    }

    private static int TryReadPort(string path)
    {
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

                if (currentSection == Section && key == "port")
                {
                    if (int.TryParse(val, out int port) && port >= 1 && port <= 65535)
                        return port;
                }
            }
        }
        catch
        {
            // 读取/解析失败，回退默认
        }
        return -1;
    }
}
