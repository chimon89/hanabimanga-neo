using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class SearchPage : Page
    {
        public SearchPageViewModel ViewModel { get; } = new();

        public SearchPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.SearchAsync(e.Parameter as string);
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
