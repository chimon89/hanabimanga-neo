using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using hanabimanga.Controls;
using hanabimanga.Models;
using hanabimanga.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace hanabimanga.Pages
{
    /// <summary>
    /// 独立的公告查看窗口:
    /// - 数据来源 Supabase announcements 表(public SELECT 允许)
    /// - OverlappedPresenter 关闭系统标题栏(无 OS caption => 不可拖动),
    ///   也禁用 resize/minimize/maximize
    /// - 自定义 TitleBar 在 XAML 顶部,仅含标题文字和关闭按钮
    /// - 居中在 MainWindow 上(创建时 snapshot 位置,不跟随)
    /// </summary>
    public sealed partial class AnnouncementWindow : Window
    {
        // 9:16 竖向窗口,高度上限(物理像素);实际高度还会按显示器工作区收窄
        private const int MaxWindowHeight = 1160;

        // 防止窗口被 GC 回收而提前关闭
        private static readonly List<AnnouncementWindow> _alive = new();

        public AnnouncementWindow()
        {
            InitializeComponent();
            ConfigurePresenter();
            CenterOnMainWindow();

            _alive.Add(this);
            Closed += (_, _) => _alive.Remove(this);
        }

        public async Task LoadAsync(string announcementId)
        {
            if (string.IsNullOrWhiteSpace(announcementId))
            {
                ShowError("公告 ID 为空。");
                return;
            }

            if (!SupabaseService.Instance.IsInitialized)
            {
                ShowError("Supabase 尚未初始化,无法加载公告。");
                return;
            }

            try
            {
                var announcement = await SupabaseService.Instance.GetAnnouncementAsync(announcementId);
                if (announcement == null)
                {
                    ShowError($"未找到 ID 为 {announcementId} 的公告。");
                    return;
                }

                Apply(announcement);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[announcements] load failed: {ex}");
                ShowError(ex.Message);
            }
        }

        private void Apply(Announcement announcement)
        {
            var displayTitle = string.IsNullOrWhiteSpace(announcement.Title) ? "公告" : announcement.Title;
            Title = displayTitle;
            WindowTitleText.Text = displayTitle;

            TitleText.Text = announcement.Title;
            (TypeBadgeText.Text, TypeBadge.Background) = MapType(announcement.AnnouncementType);

            if (announcement.CreatedAt is { } createdAt)
            {
                CreatedAtText.Text = createdAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            }
            else
            {
                CreatedAtText.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrWhiteSpace(announcement.Content))
            {
                SummaryText.Text = announcement.Content;
            }
            else
            {
                SummaryText.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrWhiteSpace(announcement.DetailContent))
            {
                MarkdownRenderer.Render(DetailPanel, announcement.DetailContent);
                DetailPanel.Visibility = Visibility.Visible;
                DetailDivider.Visibility = Visibility.Visible;
            }

            if (!string.IsNullOrWhiteSpace(announcement.ActionUrl))
            {
                ActionButton.Content = string.IsNullOrWhiteSpace(announcement.ActionText)
                    ? "查看详情"
                    : announcement.ActionText;
                ActionButton.Tag = announcement.ActionUrl;
                ActionButton.Visibility = Visibility.Visible;
            }

            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            ErrorBar.IsOpen = false;
            ContentPanel.Visibility = Visibility.Visible;
        }

        private void ShowError(string message)
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            ContentPanel.Visibility = Visibility.Collapsed;
            ErrorBar.Message = message;
            ErrorBar.IsOpen = true;
        }

        private async void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string raw ||
                string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                await Launcher.LaunchUriAsync(uri);
            }
            else
            {
                Debug.WriteLine($"[announcements] action_url 非绝对地址,跳过: {raw}");
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void CloseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            Close();
        }

        private void ConfigurePresenter()
        {
            try
            {
                var appWindow = GetAppWindow(this);
                if (appWindow.Presenter is OverlappedPresenter presenter)
                {
                    // 去掉系统标题栏:无 caption 区域 => 用户无法拖动
                    presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                    presenter.IsMinimizable = false;
                }

                // 9:16 竖向比例;高度不超过显示器工作区的 92%,避免超出屏幕
                var workArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
                var height = Math.Min(MaxWindowHeight, (int)(workArea.Height * 0.92));
                var width = height * 9 / 16;
                appWindow.Resize(new SizeInt32(width, height));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[announcements] configure presenter failed: {ex.Message}");
            }
        }

        private void CenterOnMainWindow()
        {
            try
            {
                if (App.MainWindow is null) return;

                var appWindow = GetAppWindow(this);
                var mainAppWindow = GetAppWindow(App.MainWindow);

                var mainPos = mainAppWindow.Position;
                var mainSize = mainAppWindow.Size;
                var mySize = appWindow.Size;

                var x = mainPos.X + (mainSize.Width - mySize.Width) / 2;
                var y = mainPos.Y + (mainSize.Height - mySize.Height) / 2;
                appWindow.Move(new PointInt32(x, y));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[announcements] center failed: {ex.Message}");
            }
        }

        private static AppWindow GetAppWindow(Window window)
        {
            var handle = WindowNative.GetWindowHandle(window);
            var windowId = Win32Interop.GetWindowIdFromWindow(handle);
            return AppWindow.GetFromWindowId(windowId);
        }

        private (string Label, Brush Background) MapType(string type)
        {
            var resources = Application.Current.Resources;
            return type?.Trim().ToLowerInvariant() switch
            {
                "warning" => ("警示",
                    (Brush)resources["SystemFillColorCautionBrush"]),
                "error" => ("重要",
                    (Brush)resources["SystemFillColorCriticalBrush"]),
                "success" => ("通知",
                    (Brush)resources["SystemFillColorSuccessBrush"]),
                _ => ("公告",
                    (Brush)resources["AccentFillColorDefaultBrush"]),
            };
        }
    }
}
