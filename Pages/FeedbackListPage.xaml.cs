using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class FeedbackListPage : Page
    {
        private static readonly (string Label, string? Value)[] DomainOptions =
        {
            ("全部", null),
            ("功能反馈", FeedbackOptions.DomainDev),
            ("资源反馈", FeedbackOptions.DomainOps),
        };

        private static readonly (string Label, string? Value)[] StatusOptions =
        {
            ("全部状态", null),
            ("已记录", "RECORDED"),
            ("跟踪中", "TRACKING"),
            ("进行中", "IN_PROGRESS"),
            ("已完成", "COMPLETED"),
        };

        private static readonly (string Label, string Value)[] SortOptions =
        {
            ("最多共鸣", "votes"),
            ("最近更新", "updated"),
            ("最新提交", "newest"),
            ("最早提交", "oldest"),
        };

        public FeedbackListPageViewModel ViewModel { get; } = new();

        private bool _chipsBuilt;

        public FeedbackListPage()
        {
            InitializeComponent();
            ActualThemeChanged += FeedbackListPage_ActualThemeChanged;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            BuildChipRows();
            _chipsBuilt = true;
            await ViewModel.LoadAsync();
        }

        private void FeedbackListPage_ActualThemeChanged(FrameworkElement sender, object args)
        {
            if (_chipsBuilt) BuildChipRows();
        }

        private void BuildChipRows()
        {
            BuildChipRow(
                DomainChips,
                DomainOptions,
                option => option.Label,
                option => option.Value == ViewModel.SelectedDomain,
                option => { ViewModel.SelectedDomain = option.Value; _ = ReloadAsync(); });

            BuildChipRow(
                StatusChips,
                StatusOptions,
                option => option.Label,
                option => option.Value == ViewModel.SelectedStatus,
                option => { ViewModel.SelectedStatus = option.Value; _ = ReloadAsync(); });

            BuildChipRow(
                SortChips,
                SortOptions,
                option => option.Label,
                option => option.Value == ViewModel.SortKey,
                option => { ViewModel.SortKey = option.Value; _ = ReloadAsync(); });
        }

        private async Task ReloadAsync()
        {
            FeedbackScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
            await ViewModel.LoadAsync();
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

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.LoadAsync();
        }

        private void NewFeedbackButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(FeedbackSubmitPage));
        }

        private async void VoteButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is FeedbackTicket ticket)
            {
                await ViewModel.ToggleVoteAsync(ticket);
            }
        }

        private void TicketCard_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (IsInsideButton(e.OriginalSource as DependencyObject))
            {
                return;
            }

            if ((sender as FrameworkElement)?.Tag is FeedbackTicket ticket)
            {
                Frame.Navigate(typeof(FeedbackDetailPage), ticket);
            }
        }

        private static bool IsInsideButton(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is ButtonBase)
                {
                    return true;
                }

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }
    }
}
