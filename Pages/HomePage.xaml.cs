using System;
using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class HomePage : Page
    {
        public HomePageViewModel ViewModel { get; } = new();

        private DispatcherTimer? _bannerTimer;

        public HomePage()
        {
            InitializeComponent();
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
    }
}
