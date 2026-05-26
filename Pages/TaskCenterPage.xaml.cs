using hanabimanga.ViewModels;
using hanabimanga.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class TaskCenterPage : Page
    {
        public TaskCenterViewModel ViewModel { get; } = new();

        public TaskCenterPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (!SupabaseService.Instance.IsSignedIn)
            {
                RedirectToLogin();
                return;
            }

            await ViewModel.LoadAsync();
        }

        private void RedirectToLogin()
        {
            if (App.MainWindow is hanabimanga.MainWindow mainWindow)
            {
                mainWindow.ShowAccountFlyout();
            }

            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
            else
            {
                Frame.Navigate(typeof(HomePage));
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.RefreshAsync();
        }

        private async void SignInButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.ClaimSignInAsync();
        }

        private void PointDetailButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(PointsDetailPage));
        }

        private void PointStoreButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(PointsStorePage));
        }
    }
}
