using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.Services;

namespace hanabimanga.ViewModels
{
    public enum ReaderViewMode
    {
        Page,
        Waterfall,
    }

    public sealed class ComicReaderPageViewModel : INotifyPropertyChanged
    {
        private ComicReaderDocument? _document;
        private bool _isLoading;
        private bool _hasRenderedFirstImage;
        private string? _errorMessage;
        private int _currentPage = 1;
        private ReaderViewMode _viewMode = ReaderViewMode.Page;

        public ObservableCollection<ReaderPageImage> Pages { get; } = new();

        public ComicChapter? PreviousChapter => _document?.PreviousChapter;
        public ComicChapter? NextChapter => _document?.NextChapter;

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading == value) return;
                _isLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBusy));
            }
        }

        public bool IsBusy => IsLoading || (HasReader && !_hasRenderedFirstImage);

        public ReaderViewMode ViewMode
        {
            get => _viewMode;
            private set
            {
                if (_viewMode == value) return;
                _viewMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPageMode));
                OnPropertyChanged(nameof(IsWaterfallMode));
                OnPropertyChanged(nameof(ShowPageContent));
                OnPropertyChanged(nameof(ShowWaterfallContent));
                OnPropertyChanged(nameof(CanGoPreviousPage));
                OnPropertyChanged(nameof(CanGoNextPage));
                OnPropertyChanged(nameof(ViewModeLabel));
            }
        }

        public bool IsPageMode => ViewMode == ReaderViewMode.Page;
        public bool IsWaterfallMode => ViewMode == ReaderViewMode.Waterfall;
        public bool ShowPageContent => HasReader && IsPageMode;
        public bool ShowWaterfallContent => HasReader && IsWaterfallMode;
        public string ViewModeLabel => IsWaterfallMode ? "瀑布流模式" : "翻页模式";

        public void SetViewMode(ReaderViewMode mode) => ViewMode = mode;

        public string? ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (_errorMessage == value) return;
                _errorMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
            }
        }

        public int CurrentPage
        {
            get => _currentPage;
            private set
            {
                if (_currentPage == value) return;
                _currentPage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentPageImage));
                OnPropertyChanged(nameof(CurrentPageUrl));
                OnPropertyChanged(nameof(CanGoPreviousPage));
                OnPropertyChanged(nameof(CanGoNextPage));
            }
        }

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public bool HasReader => _document != null && Pages.Count > 0;
        public bool UsesWebFallback => _document?.UsesWebFallback == true;
        public bool CanGoPrevious => PreviousChapter != null;
        public bool CanGoNext => NextChapter != null;
        public bool CanGoPreviousPage => IsPageMode && CurrentPage > 1;
        public bool CanGoNextPage => IsPageMode && CurrentPage < TotalPages;
        public int TotalPages => Pages.Count;
        public string ComicTitle => _document?.ComicTitle ?? "漫画阅读";
        public string ChapterTitle => _document?.Chapter.Title ?? "章节";
        public string TitleBarTitle => $"{ComicTitle} · {ChapterTitle}";
        public ReaderPageImage? CurrentPageImage => Pages.ElementAtOrDefault(CurrentPage - 1);
        public string CurrentPageUrl => CurrentPageImage?.Url ?? "";

        public async Task LoadAsync(ComicReaderNavigationParameter? parameter)
        {
            if (IsLoading) return;

            if (!SupabaseService.Instance.IsInitialized)
            {
                ErrorMessage = "Supabase 未初始化:请检查 appsettings.local.json 中的 Url / AnonKey。";
                return;
            }

            if (parameter == null || parameter.ComicId <= 0 || parameter.ChapterId <= 0)
            {
                ErrorMessage = "缺少章节参数,无法打开阅读器。";
                return;
            }

            IsLoading = true;
            _hasRenderedFirstImage = false;
            ErrorMessage = null;
            _document = null;
            Pages.Clear();
            CurrentPage = 1;
            RefreshAll();

            try
            {
                _document = await SupabaseService.Instance.GetComicReaderAsync(
                    parameter.ComicId,
                    parameter.ChapterId);

                foreach (var page in _document.Pages)
                {
                    Pages.Add(page);
                }
                CurrentPage = Pages.Count > 0 ? 1 : 0;

                RefreshAll();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void OnFirstImageRendered()
        {
            if (_hasRenderedFirstImage) return;
            _hasRenderedFirstImage = true;
            OnPropertyChanged(nameof(IsBusy));
        }

        public bool GoToPreviousPage()
        {
            if (!CanGoPreviousPage) return false;

            CurrentPage--;
            return true;
        }

        public bool GoToNextPage()
        {
            if (!CanGoNextPage) return false;

            CurrentPage++;
            return true;
        }

        private void RefreshAll()
        {
            OnPropertyChanged(nameof(HasReader));
            OnPropertyChanged(nameof(UsesWebFallback));
            OnPropertyChanged(nameof(CanGoPrevious));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanGoPreviousPage));
            OnPropertyChanged(nameof(CanGoNextPage));
            OnPropertyChanged(nameof(TotalPages));
            OnPropertyChanged(nameof(ComicTitle));
            OnPropertyChanged(nameof(ChapterTitle));
            OnPropertyChanged(nameof(TitleBarTitle));
            OnPropertyChanged(nameof(CurrentPageImage));
            OnPropertyChanged(nameof(CurrentPageUrl));
            OnPropertyChanged(nameof(PreviousChapter));
            OnPropertyChanged(nameof(NextChapter));
            OnPropertyChanged(nameof(ShowPageContent));
            OnPropertyChanged(nameof(ShowWaterfallContent));
            OnPropertyChanged(nameof(IsBusy));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
