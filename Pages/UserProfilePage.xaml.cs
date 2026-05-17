using System;
using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class UserProfilePage : Page
    {
        public UserProfilePageViewModel ViewModel { get; } = new();
        public event EventHandler? TitleBarInfoChanged;

        public string TitleBarCategory => "用户主页";
        public string TitleBarTitle => ViewModel.TitleBarTitle;

        public UserProfilePage()
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
            TitleBarInfoChanged?.Invoke(this, EventArgs.Empty);
        }

        private void EditProfileButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(UserSettingsPage));
        }

        private void TimelineComic_Click(object sender, RoutedEventArgs e)
        {
            var comicId = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrWhiteSpace(comicId)) return;

            Frame.Navigate(typeof(ComicDetailPage), comicId);
        }
    }
}
