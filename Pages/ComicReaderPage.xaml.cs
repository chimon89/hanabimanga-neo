using System;
using hanabimanga.Models;
using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace hanabimanga.Pages
{
    public sealed partial class ComicReaderPage : Page
    {
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

        private void ReaderImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            ViewModel.OnFirstImageRendered();
        }

        private void ReaderImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            // 即使加载失败也让 loading 收起,避免一直转圈
            ViewModel.OnFirstImageRendered();
            System.Diagnostics.Debug.WriteLine($"[reader] image failed: {e.ErrorMessage}");
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

        private void PageModeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SetViewMode(ReaderViewMode.Page);
            ReaderScrollViewer.ChangeView(null, 0, null, true);
        }

        private void WaterfallModeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SetViewMode(ReaderViewMode.Waterfall);
            ReaderScrollViewer.ChangeView(null, 0, null, true);
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
