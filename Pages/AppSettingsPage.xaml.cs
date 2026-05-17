using System;
using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class AppSettingsPage : Page
    {
        public AppSettingsPageViewModel ViewModel { get; } = new();

        public AppSettingsPage()
        {
            InitializeComponent();
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

        private async void ReaderCacheToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.IsLoaded || sender is not ToggleSwitch toggle) return;
            await ViewModel.SetReaderCacheAsync(toggle.IsOn);
        }

        private async void ReaderPreloadToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.IsLoaded || sender is not ToggleSwitch toggle) return;
            await ViewModel.SetReaderPreloadAsync(toggle.IsOn);
        }

        private async void PreloadPageCountSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!ViewModel.IsLoaded) return;

            var value = (int)Math.Round(e.NewValue);
            await ViewModel.SetPreloadPageCountAsync(value);
        }

        private async void ReaderViewModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ViewModel.IsLoaded || sender is not ComboBox comboBox) return;
            await ViewModel.SetReaderViewModeAsync(comboBox.SelectedIndex);
        }

        private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.ClearCacheAsync();
        }

        private async void DeleteDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string id) return;
            await ViewModel.DeleteDownloadAsync(id);
        }
    }
}
