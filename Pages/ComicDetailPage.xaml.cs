using System;
using hanabimanga.Models;
using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class ComicDetailPage : Page
    {
        public ComicDetailPageViewModel ViewModel { get; } = new();
        public event System.EventHandler? TitleBarInfoChanged;

        public string TitleBarTitle => ViewModel.Title;
        public string TitleBarCategory => ViewModel.CategoryName;

        public ComicDetailPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            TitleBarInfoChanged?.Invoke(this, System.EventArgs.Empty);
            await ViewModel.LoadAsync(e.Parameter as string);
            TitleBarInfoChanged?.Invoke(this, System.EventArgs.Empty);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private void CategoryButton_Click(object sender, RoutedEventArgs e)
        {
            var category = (sender as FrameworkElement)?.Tag as string;
            if (!string.IsNullOrWhiteSpace(category))
            {
                ViewModel.SelectCategory(category);
            }
        }

        private void RangeButton_Click(object sender, RoutedEventArgs e)
        {
            var key = (sender as FrameworkElement)?.Tag as string;
            var parts = key?.Split(':');
            if (parts?.Length == 2
                && int.TryParse(parts[0], out var start)
                && int.TryParse(parts[1], out var end))
            {
                ViewModel.SelectRange(start, end);
            }
        }

        private void SortButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ToggleSortDirection();
        }

        private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.ToggleFavoriteAsync();
        }

        private async void LikeButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.ToggleLikeAsync();
        }

        private async void RatingButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new RatingDialog(ViewModel.CurrentUserRating)
            {
                XamlRoot = XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && dialog.SelectedScore >= 1)
            {
                await ViewModel.SubmitRatingAsync(dialog.SelectedScore);
            }
        }

        private void ChapterButton_Click(object sender, RoutedEventArgs e)
        {
            var key = (sender as FrameworkElement)?.Tag as string;
            var parts = key?.Split(':');
            if (parts?.Length == 2
                && long.TryParse(parts[0], out var comicId)
                && long.TryParse(parts[1], out var chapterId))
            {
                Frame.Navigate(typeof(ComicReaderPage), new ComicReaderNavigationParameter
                {
                    ComicId = comicId,
                    ChapterId = chapterId,
                });
            }
        }

        private void CommentsButton_Click(object sender, RoutedEventArgs e)
        {
            var parameter = ViewModel.CreateCommentsNavigationParameter();
            if (parameter == null) return;

            Frame.Navigate(typeof(ComicCommentsPage), parameter);
        }
    }
}
