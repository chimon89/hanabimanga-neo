using System.Linq;
using hanabimanga.Models;
using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class HistoryPage : Page
    {
        public HistoryPageViewModel ViewModel { get; } = new();

        public HistoryPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.LoadAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.LoadAsync();
        }

        private void HistoryItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ComicListItem item ||
                item.ChapterId is not { } chapterId)
            {
                return;
            }

            Frame.Navigate(typeof(ComicReaderPage), new ComicReaderNavigationParameter
            {
                ComicId = item.ComicId,
                ChapterId = chapterId,
                StartPage = item.StartPage,
            });
        }
    }
}
