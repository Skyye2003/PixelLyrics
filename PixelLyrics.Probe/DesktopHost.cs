using System;
using System.Threading;

namespace PixelLyrics.Probe;

internal static class DesktopHost
{
    public static int Run(bool startInTray = false)
    {
        using var instance = new Mutex(true, "Local\\PixelLyrics.Desktop", out bool ownsInstance);
        if (!ownsInstance)
        {
            if (!startInTray) System.Windows.MessageBox.Show("设置窗口已经在运行，请检查系统托盘。", "PixelLyrics");
            return 2;
        }
        int result = 1;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new System.Windows.Application
                {
                    ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
                };
                using var window = new DesktopWindow(startInTray);
                result = application.Run(window);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"桌面界面启动或运行失败：{exception.GetType().Name}；未输出异常正文。");
                result = 1;
            }
        }) { Name = "PixelLyrics Desktop", IsBackground = false };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }
}
