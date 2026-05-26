using hanabimanga.Models;
using hanabimanga.Services;
using hanabimanga.ViewModels;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class BookshelfPage : Page
    {
        public BookshelfPageViewModel ViewModel { get; } = new();

        public BookshelfPage()
        {
            InitializeComponent();
            BookshelfSelector.SelectedItem = HistorySelectorItem;
            Loaded += BookshelfPage_Loaded;
            Unloaded += BookshelfPage_Unloaded;
        }

        private void BookshelfPage_Loaded(object sender, RoutedEventArgs e)
        {
            SupabaseService.Instance.AuthStateChanged += SupabaseService_AuthStateChanged;
        }

        private void BookshelfPage_Unloaded(object sender, RoutedEventArgs e)
        {
            SupabaseService.Instance.AuthStateChanged -= SupabaseService_AuthStateChanged;
        }

        // 登录 / 退出后,事件可能来自后台线程,切回 UI 线程刷新书架数据。
        private void SupabaseService_AuthStateChanged(object? sender, System.EventArgs e)
        {
            DispatcherQueue.TryEnqueue(async () => await ViewModel.RefreshForAuthChangeAsync());
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.EnsureLoadedAsync(ViewModel.SelectedIndex);
        }

        private async void BookshelfSelector_SelectionChanged(
            SelectorBar sender,
            SelectorBarSelectionChangedEventArgs args)
        {
            var index = sender.Items.IndexOf(sender.SelectedItem);
            if (index < 0) return;

            ViewModel.SelectedIndex = index;
            await ViewModel.EnsureLoadedAsync(index);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.ReloadCurrentAsync();
        }

        private void HistoryItem_Click(object sender, RoutedEventArgs e)
        {
            var key = (sender as FrameworkElement)?.Tag as string;
            if (!TryCreateReaderParameter(key, out var parameter))
            {
                return;
            }

            Frame.Navigate(typeof(ComicReaderPage), parameter);
        }

        private static bool TryCreateReaderParameter(
            string? key,
            out ComicReaderNavigationParameter parameter)
        {
            parameter = new ComicReaderNavigationParameter();
            var parts = key?.Split(':');

            if (parts?.Length != 3 ||
                !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var comicId) ||
                !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var chapterId) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startPage) ||
                comicId <= 0 ||
                chapterId <= 0)
            {
                return false;
            }

            parameter = new ComicReaderNavigationParameter
            {
                ComicId = comicId,
                ChapterId = chapterId,
                StartPage = startPage > 0 ? startPage : 1,
            };
            return true;
        }

        private void ComicItem_Click(object sender, RoutedEventArgs e)
        {
            var comicId = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrWhiteSpace(comicId)) return;
            Frame.Navigate(typeof(ComicDetailPage), comicId);
        }

        private void SignIn_Click(object sender, RoutedEventArgs e)
        {
            (App.MainWindow as MainWindow)?.ShowAccountFlyout();
        }
    }
}
