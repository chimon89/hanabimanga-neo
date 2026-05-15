using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace hanabimanga.Pages
{
    /// <summary>
    /// 独立的公告查看窗口。从 Supabase announcements 表读取数据并用原生 UI 渲染,
    /// 由 HomePage 在用户点击轮播图(targetType=url 且 targetValue 是 UUID)时唤起。
    /// </summary>
    public sealed partial class AnnouncementWindow : Window
    {
        // 防止窗口被 GC 回收而提前关闭
        private static readonly List<AnnouncementWindow> _alive = new();

        public AnnouncementWindow()
        {
            InitializeComponent();
            ResizeToDefault();

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
            Title = string.IsNullOrWhiteSpace(announcement.Title) ? "公告" : announcement.Title;

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
                DetailText.Text = announcement.DetailContent;
                DetailText.Visibility = Visibility.Visible;
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

        private (string Label, Brush Background) MapType(string type)
        {
            var resources = Application.Current.Resources;
            return type?.Trim().ToLowerInvariant() switch
            {
                "warning" => ("警示",
                    (Brush)resources["SystemFillColorCautionBackgroundBrush"]),
                "error" => ("重要",
                    (Brush)resources["SystemFillColorCriticalBackgroundBrush"]),
                "success" => ("通知",
                    (Brush)resources["SystemFillColorSuccessBackgroundBrush"]),
                _ => ("公告",
                    (Brush)resources["AccentFillColorTertiaryBrush"]),
            };
        }

        private void ResizeToDefault()
        {
            try
            {
                var handle = WindowNative.GetWindowHandle(this);
                var windowId = Win32Interop.GetWindowIdFromWindow(handle);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                appWindow.Resize(new SizeInt32(900, 720));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[announcements] resize failed: {ex.Message}");
            }
        }
    }
}
