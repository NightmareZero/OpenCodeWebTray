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
                // 已有实例在运行，直接退出
                return;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApplicationContext());
        }
        finally
        {
            try { _mutex?.ReleaseMutex(); } catch { }
        }
    }
}
