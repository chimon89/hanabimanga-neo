using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class PointsDetailPage : Page
    {
        public TaskCenterViewModel ViewModel { get; } = new();

        public PointsDetailPage()
        {
            InitializeComponent();
            TransactionSelector.SelectedItem = AllItem;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.LoadAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.RefreshAsync();
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

        private void TransactionSelector_SelectionChanged(
            SelectorBar sender,
            SelectorBarSelectionChangedEventArgs args)
        {
            var filter = sender.SelectedItem switch
            {
                var item when item == IncomeItem => "income",
                var item when item == ExpenseItem => "expense",
                _ => "all",
            };

            ViewModel.SetTransactionFilter(filter);
        }
    }
}
