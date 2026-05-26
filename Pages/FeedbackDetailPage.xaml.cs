using hanabimanga.Models;
using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class FeedbackDetailPage : Page
    {
        public FeedbackDetailPageViewModel ViewModel { get; } = new();

        public FeedbackDetailPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is FeedbackTicket ticket)
            {
                ViewModel.Initialize(ticket);
                await ViewModel.LoadVotersAsync();
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private async void VoteButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.ToggleVoteAsync();
        }
    }
}
