using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using VisionInspection.Core.Services;
using VisionInspection.UI.Views;

namespace VisionInspection.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private UserManager? _userManager;
    private static int _crashDialogShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局兜底：任何未处理异常先落盘（logs/crash.log）再提示。
        // 之前 UI 线程/后台线程抛异常时进程直接消失（"连接相机闪退"）且没有任何现场可查。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("后台线程未处理异常（进程即将退出）", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("未观察的 Task 异常", args.Exception);
            args.SetObserved();
        };

        try
        {
            // 环境自检：把实际使用的配置/配方/模型目录写进 logs/startup.log。
            // 排查"界面读不到配方""检测不到目标"时，先看这一行。
            try
            {
                File.AppendAllText(AppPaths.Log("startup.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {AppPaths.Describe()}{Environment.NewLine}");
            }
            catch { /* 自检失败不影响启动 */ }

            // 初始化用户管理器
            var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "users.db");
            var dbDir = Path.GetDirectoryName(dbPath)!;
            
            // 确保目录存在
            if (!Directory.Exists(dbDir))
            {
                Directory.CreateDirectory(dbDir);
            }
            
            _userManager = new UserManager(dbPath);

            // 显示登录窗口
            var loginWindow = new LoginWindow(_userManager);
            if (loginWindow.ShowDialog() != true)
            {
                // 登录失败或取消，退出程序
                Shutdown();
                return;
            }

            // 登录成功，显示主窗口
            var mainWindow = new MainWindow(_userManager);
            mainWindow.Show();

            // ⭐ 程序启动后默认连接 Modbus（若配置允许），并启动断线低频自动重连。
            // 放到后台线程执行，避免 TCP 连接超时阻塞 UI 启动。
            _ = Task.Run(() =>
            {
                try { ModbusService.Instance.Start(); }
                catch (Exception ex) { Debug.WriteLine($"Modbus 启动连接失败：{ex.Message}"); }
            });
        }
        catch (Exception ex)
        {
            // 输出到调试窗口
            Debug.WriteLine($"程序启动失败：{ex.Message}");
            Debug.WriteLine($"堆栈跟踪：{ex.StackTrace}");
            
            // 确保在主线程上显示错误消息
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show($"程序启动失败：{ex.Message}\n\n{ex.StackTrace}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            });
            Shutdown();
        }
    }

    /// <summary>
    /// UI 线程未处理异常：记录现场并留在程序里（产线不因一次界面异常直接退出）。
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = LogCrash("UI 线程未处理异常", e.Exception);
        e.Handled = true;

        // 只弹一次，避免动画/定时器类异常每帧弹窗
        if (Interlocked.Exchange(ref _crashDialogShown, 1) != 0) return;
        try
        {
            MessageBox.Show(
                $"程序遇到一个错误，已记录到：\n{logPath}\n\n{e.Exception.GetType().Name}: {e.Exception.Message}\n\n程序会继续运行；若反复出现，请把该日志发给开发人员。",
                "运行错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* 弹窗失败不影响继续运行 */ }
    }

    /// <summary>写崩溃现场到 logs/crash.log，返回日志路径。</summary>
    private static string LogCrash(string kind, Exception? ex)
    {
        var path = AppPaths.Log("crash.log");
        try
        {
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* 日志失败不影响主流程 */ }
        return path;
    }
}
