using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class PointsStorePage : Page
    {
        public TaskCenterViewModel ViewModel { get; } = new();

        public PointsStorePage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.LoadAsync();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
                return;
            }

            Frame.Navigate(typeof(TaskCenterPage));
        }

        private void ExchangeHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(ExchangeHistoryPage));
        }

        private async void StoreItemButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string itemId) return;
            await ViewModel.RedeemAsync(itemId);
        }
    }
}
