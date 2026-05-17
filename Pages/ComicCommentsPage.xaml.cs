using System;
using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class ComicCommentsPage : Page
    {
        public ComicCommentsPageViewModel ViewModel { get; } = new();
        public event EventHandler? TitleBarInfoChanged;

        public string TitleBarCategory => "评论";
        public string TitleBarTitle => ViewModel.TitleBarTitle;

        public ComicCommentsPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            TitleBarInfoChanged?.Invoke(this, EventArgs.Empty);
            await ViewModel.LoadAsync(e.Parameter);
            TitleBarInfoChanged?.Invoke(this, EventArgs.Empty);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.ReloadAsync();
        }

        private async void SubmitCommentButton_Click(object sender, RoutedEventArgs e)
        {
            var submitted = await ViewModel.SubmitCommentAsync(
                CommentTextBox.Text,
                SpoilerCheckBox.IsChecked == true);

            if (!submitted) return;

            CommentTextBox.Text = "";
            SpoilerCheckBox.IsChecked = false;
            CommentsScrollViewer.ChangeView(null, 0, null, true);
        }

        private void SignInButton_Click(object sender, RoutedEventArgs e)
        {
            (App.MainWindow as MainWindow)?.ShowAccountFlyout();
        }

        private void CommentAuthor_Click(object sender, RoutedEventArgs e)
        {
            var userId = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrWhiteSpace(userId)) return;

            Frame.Navigate(typeof(UserProfilePage), userId);
        }
    }
}
