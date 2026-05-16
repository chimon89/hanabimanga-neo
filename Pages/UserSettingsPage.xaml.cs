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
            await ViewModel.UpdatePasswordAsync(PasswordBox.Password, PasswordConfirmBox.Password);
            if (!ViewModel.HasError)
            {
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
