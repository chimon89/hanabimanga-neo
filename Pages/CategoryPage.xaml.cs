using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using hanabimanga.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class CategoryPage : Page
    {
        // 加载更多触发阈值:距底部 600px 内即预取下一页
        private const double LoadMoreThreshold = 600;

        public CategoryPageViewModel ViewModel { get; } = new();

        private bool _chipsBuilt;

        public CategoryPage()
        {
            InitializeComponent();
            ActualThemeChanged += CategoryPage_ActualThemeChanged;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.InitializeAsync();
            BuildChipRows();
            _chipsBuilt = true;
        }

        // 主题切换时芯片画刷不会自动跟随(代码里赋的是固定画刷),重建一次以应用当前主题
        private void CategoryPage_ActualThemeChanged(FrameworkElement sender, object args)
        {
            if (_chipsBuilt)
                BuildChipRows();
        }

        private void BuildChipRows()
        {
            BuildChipRow(
                CategoryChips,
                ViewModel.Categories,
                option => option.Name,
                option => option == ViewModel.SelectedCategory,
                option => { ViewModel.SelectedCategory = option; _ = OnFilterChangedAsync(); });

            BuildChipRow(
                SortChips,
                ViewModel.Sorts,
                option => option.Name,
                option => option == ViewModel.SelectedSort,
                option => { ViewModel.SelectedSort = option; _ = OnFilterChangedAsync(); });

            BuildChipRow(
                RegionChips,
                ViewModel.Regions,
                option => option.Name,
                option => option == ViewModel.SelectedRegion,
                option => { ViewModel.SelectedRegion = option; _ = OnFilterChangedAsync(); });

            BuildChipRow(
                StatusChips,
                ViewModel.Statuses,
                option => option.Name,
                option => option == ViewModel.SelectedStatus,
                option => { ViewModel.SelectedStatus = option; _ = OnFilterChangedAsync(); });
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
                    Padding = new Thickness(10, 4, 10, 4),
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

        // 按当前实际主题从对应 ThemeDictionary 取画刷;
        // 不能用 Application.Current.Resources[key],它只返回应用启动时主题的画刷。
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

        private async Task OnFilterChangedAsync()
        {
            ResultsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
            await ViewModel.ApplyFiltersAsync();
        }

        private async void ResultsScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (e.IsIntermediate) return;
            if (sender is not ScrollViewer scrollViewer) return;

            if (scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - LoadMoreThreshold)
            {
                await ViewModel.LoadMoreAsync();
            }
        }

        private void ComicItem_Click(object sender, RoutedEventArgs e)
        {
            var comicDocumentId = (sender as FrameworkElement)?.Tag as string;
            if (!string.IsNullOrWhiteSpace(comicDocumentId))
            {
                Frame.Navigate(typeof(ComicDetailPage), comicDocumentId);
            }
        }
    }
}
