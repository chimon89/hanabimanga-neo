using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace hanabimanga.Pages
{
    public sealed partial class FeedbackSubmitPage : Page
    {
        public FeedbackSubmitPageViewModel ViewModel { get; } = new();

        private bool _suppressTitleSync;

        public FeedbackSubmitPage()
        {
            InitializeComponent();
            ActualThemeChanged += FeedbackSubmitPage_ActualThemeChanged;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ApplyDomainButtons();
            await ViewModel.LoadQuotaAsync();
        }

        private void FeedbackSubmitPage_ActualThemeChanged(FrameworkElement sender, object args)
        {
            ApplyDomainButtons();
            if (ViewModel.HasDomain) BuildCategoryChips();
        }

        private void DomainDevButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SelectedDomain = FeedbackOptions.DomainDev;
            ApplyDomainButtons();
            BuildCategoryChips();
        }

        private void DomainOpsButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SelectedDomain = FeedbackOptions.DomainOps;
            ApplyDomainButtons();
            BuildCategoryChips();
        }

        private void ApplyDomainButtons()
        {
            var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
            DomainDevButton.Style = ViewModel.IsFeatureDomain ? accent : null;
            DomainOpsButton.Style = ViewModel.IsResourceDomain ? accent : null;
        }

        private void BuildCategoryChips()
        {
            BuildChipRow(
                CategoryChips,
                ViewModel.CurrentCategories,
                category => category,
                category => category == ViewModel.SelectedCategory,
                category => { ViewModel.SelectedCategory = category; });
        }

        private void BuildChipRow<T>(
            StackPanel host,
            IReadOnlyList<T> options,
            Func<T, string> nameOf,
            Func<T, bool> isSelected,
            Action<T> onSelected)
        {
            host.Children.Clear();
            var chips = new List<(Border Chip, T Value)>();

            foreach (var option in options)
            {
                var captured = option;
                var chip = new Border
                {
                    Child = new TextBlock { Text = nameOf(option), FontSize = 12 },
                    Padding = new Thickness(12, 6, 12, 6),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                };
                chip.Tapped += (_, _) =>
                {
                    onSelected(captured);
                    foreach (var (other, value) in chips)
                        ApplyChipState(other, isSelected(value));
                };
                chip.PointerEntered += (_, _) =>
                {
                    if (!isSelected(captured))
                        chip.Background = ThemedBrush("ControlFillColorSecondaryBrush");
                };
                chip.PointerExited += (_, _) =>
                {
                    if (!isSelected(captured))
                        chip.Background = ThemedBrush("ControlFillColorDefaultBrush");
                };
                chips.Add((chip, option));
                host.Children.Add(chip);
            }

            foreach (var (chip, value) in chips)
                ApplyChipState(chip, isSelected(value));
        }

        private void ApplyChipState(Border chip, bool selected)
        {
            var text = (TextBlock)chip.Child;
            if (selected)
            {
                var accent = ThemedBrush("AccentFillColorDefaultBrush");
                chip.Background = accent;
                chip.BorderBrush = accent;
                text.Foreground = ThemedBrush("TextOnAccentFillColorPrimaryBrush");
                text.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                chip.Background = ThemedBrush("ControlFillColorDefaultBrush");
                chip.BorderBrush = ThemedBrush("CardStrokeColorDefaultBrush");
                text.Foreground = ThemedBrush("TextFillColorPrimaryBrush");
                text.FontWeight = FontWeights.Normal;
            }
        }

        private Brush ThemedBrush(string key)
        {
            var dictKey = ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
            var themeDictionaries = Application.Current.Resources.ThemeDictionaries;
            if (themeDictionaries.TryGetValue(dictKey, out var raw)
                && raw is ResourceDictionary dictionary
                && dictionary.TryGetValue(key, out var value)
                && value is Brush brush)
            {
                return brush;
            }
            return (Brush)Application.Current.Resources[key];
        }

        private async void BangumiSearchButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.SearchBangumiAsync(BangumiSearchBox.Text);
        }

        private async void BangumiSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await ViewModel.SearchBangumiAsync(BangumiSearchBox.Text);
            }
        }

        private void BangumiResult_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not BangumiBook book) return;

            ViewModel.SelectedBangumiBook = book;
            if (string.IsNullOrWhiteSpace(TitleTextBox.Text))
            {
                _suppressTitleSync = true;
                TitleTextBox.Text = ViewModel.Title;
                _suppressTitleSync = false;
            }
        }

        private void ClearBangumiButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SelectedBangumiBook = null;
        }

        private async void ComicSearchButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.SearchComicAsync(ComicSearchBox.Text);
        }

        private async void ComicSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await ViewModel.SearchComicAsync(ComicSearchBox.Text);
            }
        }

        private void ComicResult_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ComicListItem comic)
            {
                ViewModel.SelectedComic = comic;
            }
        }

        private void ClearComicButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SelectedComic = null;
        }

        private void TitleTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressTitleSync) return;
            ViewModel.Title = TitleTextBox.Text;
        }

        private void DescriptionTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.Description = DescriptionTextBox.Text;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private async void SubmitButton_Click(object sender, RoutedEventArgs e)
        {
            var submitted = await ViewModel.SubmitAsync();
            if (!submitted) return;

            await Task.Delay(900);
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }
}
