using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class LikesPage : Page
    {
        public LikesPageViewModel ViewModel { get; } = new();

        public LikesPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.LoadAsync();
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
