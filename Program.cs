using System.Threading;
using System.Windows.Forms;

namespace OpenCodeWebTray;

internal static class Program
{
    // 全局命名互斥量，保证只运行一个实例
    private const string MutexName = @"Global\OpenCodeWebTray_SingleInstance_3F7A1E";
    private static Mutex _mutex;

    [STAThread]
    private static void Main()
    {
        bool createdNew;
        _mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out createdNew);

        try
        {
            if (!createdNew)
            {
                // 已有实例在运行：给个提示，避免用户以为没启动又反复点
                MessageBox.Show(
                    "OpenCode Web Tray 已在运行，请检查系统托盘" +
                    "（若不可见，点击托盘区向上箭头展开隐藏图标）。",
                    "OpenCode Web Tray",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApplicationContext());
        }
            catch (Exception ex)
            {
                // 启动期异常写日志到 exe 同目录，避免静默崩溃无法定位
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"), ex.ToString(), System.Text.Encoding.UTF8); } catch { }
            }
        finally
        {
            try { _mutex?.ReleaseMutex(); } catch { }
        }
    }
}
