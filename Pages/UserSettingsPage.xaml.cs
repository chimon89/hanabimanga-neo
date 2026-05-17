using System;
using System.Globalization;
using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace hanabimanga.Pages
{
    public sealed partial class UserSettingsPage : Page
    {
        public UserSettingsPageViewModel ViewModel { get; } = new();

        public UserSettingsPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.LoadAsync();
            ShowSettingsList();
        }

        private void ProfileSectionButton_Click(object sender, RoutedEventArgs e)
            => ShowSettingsDetail("个人资料", ProfileDetailPanel);

        private void AvatarSectionButton_Click(object sender, RoutedEventArgs e)
            => ShowSettingsDetail("头像", AvatarDetailPanel);

        private void BannerSectionButton_Click(object sender, RoutedEventArgs e)
            => ShowSettingsDetail("个人页横幅", BannerDetailPanel);

        private void BadgesSectionButton_Click(object sender, RoutedEventArgs e)
            => ShowSettingsDetail("个人徽章", BadgesDetailPanel);

        private void EmailSectionButton_Click(object sender, RoutedEventArgs e)
            => ShowSettingsDetail("邮箱", EmailDetailPanel);

        private void PasswordSectionButton_Click(object sender, RoutedEventArgs e)
            => ShowSettingsDetail("密码", PasswordDetailPanel);

        private void BackSettingsListButton_Click(object sender, RoutedEventArgs e)
            => ShowSettingsList();

        private void ShowSettingsList()
        {
            SettingsListPanel.Visibility = Visibility.Visible;
            DetailHost.Visibility = Visibility.Collapsed;
            HideAllDetailPanels();
        }

        private void ShowSettingsDetail(string title, FrameworkElement panel)
        {
            SettingsDetailTitleTextBlock.Text = title;
            SettingsListPanel.Visibility = Visibility.Collapsed;
            DetailHost.Visibility = Visibility.Visible;
            HideAllDetailPanels();
            panel.Visibility = Visibility.Visible;
            SettingsScrollViewer.ChangeView(null, 0, null, true);
        }

        private void HideAllDetailPanels()
        {
            ProfileDetailPanel.Visibility = Visibility.Collapsed;
            AvatarDetailPanel.Visibility = Visibility.Collapsed;
            BannerDetailPanel.Visibility = Visibility.Collapsed;
            BadgesDetailPanel.Visibility = Visibility.Collapsed;
            EmailDetailPanel.Visibility = Visibility.Collapsed;
            PasswordDetailPanel.Visibility = Visibility.Collapsed;
        }

        private async void SaveProfileButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.SaveProfileAsync(UsernameBox.Text, DisplayNameBox.Text);
            RefreshAccountDisplay();
        }

        private async void AvatarPresetButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string fileName) return;
            await ViewModel.SelectPresetAvatarAsync(fileName);
            RefreshAccountDisplay();
        }

        private async void BannerPresetButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string fileName) return;
            await ViewModel.SelectPresetBannerAsync(fileName);
        }

        private async void ChooseCustomAvatarButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanUploadCustomAvatar || App.MainWindow == null) return;

            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            };
            picker.FileTypeFilter.Add(".webp");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            await ViewModel.UploadCustomAvatarAsync(file.Path);
            RefreshAccountDisplay();
        }

        private async void BadgeDisplayButton_Click(object sender, RoutedEventArgs e)
        {
            var tag = (sender as FrameworkElement)?.Tag;
            var id = tag switch
            {
                long longValue => longValue,
                int intValue => intValue,
                string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0,
            };

            if (id <= 0) return;
            await ViewModel.ToggleBadgeDisplayAsync(id);
        }

        private async void SaveEmailButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.UpdateEmailAsync(EmailBox.Text);
        }

        private async void SavePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.UpdatePasswordAsync(CurrentPasswordBox.Password, PasswordBox.Password, PasswordConfirmBox.Password);
            if (!ViewModel.HasError)
            {
                CurrentPasswordBox.Password = "";
                PasswordBox.Password = "";
                PasswordConfirmBox.Password = "";
            }
        }

        private static void RefreshAccountDisplay()
        {
            (App.MainWindow as MainWindow)?.RefreshAccountDisplay();
        }
    }
}
