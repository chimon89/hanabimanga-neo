using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class RankingPage : Page
    {
        public RankingPageViewModel ViewModel { get; } = new();

        public RankingPage()
        {
            InitializeComponent();
            RankingSelector.SelectedItem = DailyItem;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.EnsureLoadedAsync(ViewModel.SelectedIndex);
        }

        private async void RankingSelector_SelectionChanged(
            SelectorBar sender,
            SelectorBarSelectionChangedEventArgs args)
        {
            var index = sender.Items.IndexOf(sender.SelectedItem);
            if (index < 0) return;

            await ViewModel.EnsureLoadedAsync(index);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.ReloadCurrentAsync();
        }

        private void RankingItem_Click(object sender, RoutedEventArgs e)
        {
            var comicId = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrWhiteSpace(comicId)) return;
            Frame.Navigate(typeof(ComicDetailPage), comicId);
        }
    }
}
