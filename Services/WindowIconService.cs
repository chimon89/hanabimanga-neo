using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace hanabimanga.Services
{
    internal static class WindowIconService
    {
        private const string AppIconFileName = "AppIcon.ico";

        public static void ApplyTo(Window window, AppWindow? appWindow = null)
        {
            try
            {
                appWindow ??= GetAppWindow(window);
                var iconPath = ResolveIconPath();
                if (string.IsNullOrWhiteSpace(iconPath)) return;

                appWindow.SetIcon(iconPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[window-icon] set icon failed: {ex.Message}");
            }
        }

        private static string? ResolveIconPath()
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", AppIconFileName);
            return File.Exists(iconPath) ? iconPath : null;
        }

        private static AppWindow GetAppWindow(Window window)
        {
            var handle = WindowNative.GetWindowHandle(window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
            return AppWindow.GetFromWindowId(windowId);
        }
    }
}
