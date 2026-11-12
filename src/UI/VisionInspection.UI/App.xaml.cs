using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using VisionInspection.Core.Services;
using VisionInspection.UI.Views;

namespace VisionInspection.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private UserManager? _userManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
}
