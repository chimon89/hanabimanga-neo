using System;
using hanabimanga.Models;
using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace hanabimanga.Pages
{
    public sealed partial class HomePage : Page
    {
        public HomePageViewModel ViewModel { get; } = new();

        private DispatcherTimer? _bannerTimer;

        public HomePage()
        {
            InitializeComponent();
            RankingSelector.SelectedItem = RankingDailyItem;
        }

        private void RankingSelector_SelectionChanged(
            SelectorBar sender,
            SelectorBarSelectionChangedEventArgs args)
        {
            var index = sender.Items.IndexOf(sender.SelectedItem);
            if (index < 0) return;

            ViewModel.SelectedRankingIndex = index;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.LoadAsync();
            StartBannerAutoplay();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            StopBannerAutoplay();
        }

        private void StartBannerAutoplay()
        {
            StopBannerAutoplay();

            _bannerTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(4),
            };
            _bannerTimer.Tick += BannerTimer_Tick;
            _bannerTimer.Start();
        }

        private void StopBannerAutoplay()
        {
            if (_bannerTimer == null) return;
            _bannerTimer.Stop();
            _bannerTimer.Tick -= BannerTimer_Tick;
            _bannerTimer = null;
        }

        private void BannerTimer_Tick(object? sender, object e)
        {
            var count = ViewModel.Banners.Count;
            if (count <= 1) return;

            var next = (BannerFlipView.SelectedIndex + 1) % count;
            BannerFlipView.SelectedIndex = next;
        }

        private void ShuffleRecommendations_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ShowNextRecommendations();
        }

        private void ComicItem_Click(object sender, RoutedEventArgs e)
        {
            var comicId = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrWhiteSpace(comicId))
            {
                System.Diagnostics.Debug.WriteLine("[home] comic item click ignored: empty item id");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[home] navigate comic detail: {comicId}");
            Frame.Navigate(typeof(ComicDetailPage), comicId);
        }

        private static void OpenAnnouncementWindow(string announcementId)
        {
            // 立即弹出窗口(带 ProgressRing),数据加载在 LoadAsync 内异步完成
            var window = new AnnouncementWindow();
            window.Activate();
            _ = window.LoadAsync(announcementId);
        }

        private async void BannerItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not HomeFeedBanner banner) return;

            var type = banner.TargetType?.Trim().ToLowerInvariant();
            var value = banner.TargetValue?.Trim();
            System.Diagnostics.Debug.WriteLine($"[home] banner tapped: type={type}, value={value}");

            if (string.IsNullOrWhiteSpace(value)) return;

            // 数字 → 漫画详情页
            if (long.TryParse(value, out var comicId) && comicId > 0)
            {
                Frame.Navigate(typeof(ComicDetailPage), value);
                return;
            }

            // 裸 UUID → 公告窗口
            if (Guid.TryParse(value, out var announcementId))
            {
                OpenAnnouncementWindow(announcementId.ToString());
                return;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                // 站内公告链接(.../announcements/{uuid})→ 应用内公告窗口
                if (TryGetAnnouncementId(uri, out var urlAnnouncementId))
                {
                    OpenAnnouncementWindow(urlAnnouncementId);
                    return;
                }

                // 真实外链 → 系统浏览器
                if (type == "url")
                {
                    await Launcher.LaunchUriAsync(uri);
                    return;
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[home] banner 目标无法识别,跳过: type={type}, value={value}");
        }

        // 识别站内公告链接:路径形如 .../announcements/{uuid}
        private static bool TryGetAnnouncementId(Uri uri, out string announcementId)
        {
            announcementId = "";
            var segments = uri.Segments;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (segments[i].Trim('/').Equals("announcements", StringComparison.OrdinalIgnoreCase) &&
                    Guid.TryParse(segments[i + 1].Trim('/'), out var id))
                {
                    announcementId = id.ToString();
                    return true;
                }
            }
            return false;
        }
    }
}
