using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class DiscoverPage : Page
    {
        public DiscoverPageViewModel ViewModel { get; } = new();

        public DiscoverPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.LoadRandomAsync();
        }

        private async void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            DiscoverSearchBox.Text = "";
            await ViewModel.LoadRandomAsync();
        }

        private async void DiscoverSearchBox_QuerySubmitted(
            AutoSuggestBox sender,
            AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            await ViewModel.SearchAsync(args.QueryText);
        }

        private void ComicItem_Click(object sender, RoutedEventArgs e)
        {
            var comicDocumentId = (sender as FrameworkElement)?.Tag as string;
            if (!string.IsNullOrWhiteSpace(comicDocumentId))
            {
                Frame.Navigate(typeof(ComicDetailPage), comicDocumentId);
            }
        }
    }
}
