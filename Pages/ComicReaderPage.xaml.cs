using System;
using hanabimanga.Models;
using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class ComicReaderPage : Page
    {
        private const double AutoAdvanceThreshold = 28;

        public ComicReaderPageViewModel ViewModel { get; } = new();
        public event EventHandler? TitleBarInfoChanged;

        public string TitleBarCategory => "阅读器";
        public string TitleBarTitle => ViewModel.TitleBarTitle;

        public ComicReaderPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            TitleBarInfoChanged?.Invoke(this, EventArgs.Empty);
            await ViewModel.LoadAsync(e.Parameter as ComicReaderNavigationParameter);
            ReaderScrollViewer.ChangeView(null, 0, null, true);
            TitleBarInfoChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ReaderScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (e.IsIntermediate || ReaderScrollViewer.ScrollableHeight <= AutoAdvanceThreshold)
                return;

            var remaining = ReaderScrollViewer.ScrollableHeight - ReaderScrollViewer.VerticalOffset;
            if (remaining <= AutoAdvanceThreshold && ViewModel.GoToNextPage())
            {
                ReaderScrollViewer.ChangeView(null, 0, null, true);
            }
        }

        private void PreviousChapterButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToChapter(ViewModel.PreviousChapter);
        }

        private void NextChapterButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToChapter(ViewModel.NextChapter);
        }

        private void PreviousPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.GoToPreviousPage())
            {
                ReaderScrollViewer.ChangeView(null, 0, null, true);
            }
        }

        private void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.GoToNextPage())
            {
                ReaderScrollViewer.ChangeView(null, 0, null, true);
            }
        }

        private void ScrollTopButton_Click(object sender, RoutedEventArgs e)
        {
            ReaderScrollViewer.ChangeView(null, 0, null);
        }

        private void NavigateToChapter(ComicChapter? chapter)
        {
            if (chapter == null) return;

            Frame.Navigate(typeof(ComicReaderPage), new ComicReaderNavigationParameter
            {
                ComicId = chapter.ComicId,
                ChapterId = chapter.Id,
            });
        }
    }
}
