using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.Services;

namespace hanabimanga.ViewModels
{
    public sealed class ComicReaderPageViewModel : INotifyPropertyChanged
    {
        private ComicReaderDocument? _document;
        private bool _isLoading;
        private string? _errorMessage;
        private int _currentPage = 1;
        private List<ReaderPageImage> _pageImages = new();

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
            }
        }

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
        public bool HasReader => _document != null && _pageImages.Count > 0;
        public bool UsesWebFallback => _document?.UsesWebFallback == true;
        public bool CanGoPrevious => PreviousChapter != null;
        public bool CanGoNext => NextChapter != null;
        public bool CanGoPreviousPage => CurrentPage > 1;
        public bool CanGoNextPage => CurrentPage < TotalPages;
        public int TotalPages => _pageImages.Count;
        public string ComicTitle => _document?.ComicTitle ?? "漫画阅读";
        public string ChapterTitle => _document?.Chapter.Title ?? "章节";
        public string TitleBarTitle => $"{ComicTitle} · {ChapterTitle}";
        public ReaderPageImage? CurrentPageImage => _pageImages.ElementAtOrDefault(CurrentPage - 1);
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
            ErrorMessage = null;
            _document = null;
            _pageImages = new List<ReaderPageImage>();
            CurrentPage = 1;
            RefreshAll();

            try
            {
                _document = await SupabaseService.Instance.GetComicReaderAsync(
                    parameter.ComicId,
                    parameter.ChapterId);

                _pageImages = _document.Pages;
                CurrentPage = _pageImages.Count > 0 ? 1 : 0;

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
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
