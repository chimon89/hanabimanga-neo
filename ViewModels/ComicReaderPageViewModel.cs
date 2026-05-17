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
        private ComicReaderNavigationParameter? _lastParameter;
        private bool _isLoading;
        private bool _hasRenderedFirstImage;
        private string? _errorMessage;
        private string? _downloadFeedbackMessage;
        private string? _downloadErrorMessage;
        private int _currentPage = 1;
        private ReaderViewMode _viewMode = ReaderViewMode.Page;
        private bool _useUpscaled;
        private bool _isDownloading;

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
                OnPropertyChanged(nameof(CanToggleUpscaled));
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

        public void SetViewMode(ReaderViewMode mode)
        {
            ApplyViewMode(mode);
            _ = SaveViewModePreferenceAsync(mode);
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
        public ComicReaderDocument? CurrentDocument => _document;

        public bool IsDownloading
        {
            get => _isDownloading;
            private set
            {
                if (_isDownloading == value) return;
                _isDownloading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanDownloadCurrentChapter));
                OnPropertyChanged(nameof(DownloadButtonText));
            }
        }

        public bool CanDownloadCurrentChapter => HasReader && !IsLoading && !IsDownloading;
        public string DownloadButtonText => IsDownloading ? "下载中" : "下载";

        public string? DownloadFeedbackMessage
        {
            get => _downloadFeedbackMessage;
            private set
            {
                if (_downloadFeedbackMessage == value) return;
                _downloadFeedbackMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDownloadFeedback));
            }
        }

        public bool HasDownloadFeedback => !string.IsNullOrWhiteSpace(DownloadFeedbackMessage);

        public string? DownloadErrorMessage
        {
            get => _downloadErrorMessage;
            private set
            {
                if (_downloadErrorMessage == value) return;
                _downloadErrorMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDownloadError));
            }
        }

        public bool HasDownloadError => !string.IsNullOrWhiteSpace(DownloadErrorMessage);

        // AI 超分(高清)相关
        public bool UseUpscaled
        {
            get => _useUpscaled;
            private set
            {
                if (_useUpscaled == value) return;
                _useUpscaled = value;
                OnPropertyChanged();
            }
        }

        public bool HasUpscaledAvailable => _document?.HasUpscaled == true;
        public bool IsUpscaledLoaded => _document?.IsUpscaled == true;
        public bool CanToggleUpscaled => !IsLoading && HasUpscaledAvailable;
        public bool ShowUpscaledQuota => _document?.Quota is { IsVip: false };
        public string UpscaledQuotaText => _document?.Quota is { IsVip: false } q
            ? $"AI 超分今日剩余 {q.Remaining}/{q.DailyLimit}"
            : "";

        public async Task LoadAsync(ComicReaderNavigationParameter? parameter)
        {
            _lastParameter = parameter;
            await ApplyPreferredViewModeAsync();
            var useUpscaled = _useUpscaled || await ShouldUseUpscaledByDefaultAsync(parameter);
            UseUpscaled = useUpscaled;
            await LoadCoreAsync(parameter, useUpscaled);

            if (_document != null && _document.IsUpscaled != UseUpscaled)
            {
                UseUpscaled = _document.IsUpscaled;
            }
        }

        public async Task SetUpscaledAsync(bool useUpscaled)
        {
            if (IsLoading) return;
            if (_useUpscaled == useUpscaled) return;

            // 提前更新以让 UI 即时反馈(若失败会还原)
            UseUpscaled = useUpscaled;
            await LoadCoreAsync(_lastParameter, useUpscaled);

            // 服务端可能因为漫画无超分而强制回退;以实际加载的资源为准
            if (_document != null && _document.IsUpscaled != useUpscaled)
            {
                UseUpscaled = _document.IsUpscaled;
            }
            else if (HasError)
            {
                // 失败时回滚 toggle
                UseUpscaled = !useUpscaled;
            }
        }

        private async Task LoadCoreAsync(ComicReaderNavigationParameter? parameter, bool useUpscaled)
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
            DownloadFeedbackMessage = null;
            DownloadErrorMessage = null;
            _document = null;
            Pages.Clear();
            CurrentPage = 1;
            RefreshAll();

            try
            {
                _document = await SupabaseService.Instance.GetComicReaderAsync(
                    parameter.ComicId,
                    parameter.ChapterId,
                    useUpscaled);

                await ReaderStorageService.Instance.ApplyCachedPagesAsync(_document);

                foreach (var page in _document.Pages)
                {
                    Pages.Add(page);
                }
                CurrentPage = Pages.Count > 0
                    ? Math.Clamp(parameter.StartPage, 1, Pages.Count)
                    : 0;

                RefreshAll();
                StartPreloadAroundCurrentPage();
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

        private async Task<bool> ShouldUseUpscaledByDefaultAsync(ComicReaderNavigationParameter? parameter)
        {
            if (parameter == null || parameter.ComicId <= 0) return false;
            if (!SupabaseService.Instance.IsInitialized) return false;

            try
            {
                return await SupabaseService.Instance.ShouldAutoUseUpscaledAsync(parameter.ComicId);
            }
            catch
            {
                // 自动高清只是增强体验,预判失败时保持标清默认加载。
                return false;
            }
        }

        private async Task ApplyPreferredViewModeAsync()
        {
            try
            {
                var settings = await ReaderStorageService.Instance.LoadSettingsAsync();
                ApplyViewMode(ParseViewMode(settings.ReaderViewMode));
            }
            catch
            {
                ApplyViewMode(ReaderViewMode.Page);
            }
        }

        private void ApplyViewMode(ReaderViewMode mode)
        {
            ViewMode = mode;
        }

        private async Task SaveViewModePreferenceAsync(ReaderViewMode mode)
        {
            try
            {
                var settings = await ReaderStorageService.Instance.LoadSettingsAsync();
                settings.ReaderViewMode = ToStorageViewMode(mode);
                await ReaderStorageService.Instance.SaveSettingsAsync(settings);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[reader-settings] save view mode failed: {ex.Message}");
            }
        }

        private static ReaderViewMode ParseViewMode(string? mode)
            => string.Equals(mode, "waterfall", StringComparison.OrdinalIgnoreCase)
                ? ReaderViewMode.Waterfall
                : ReaderViewMode.Page;

        private static string ToStorageViewMode(ReaderViewMode mode)
            => mode == ReaderViewMode.Waterfall ? "waterfall" : "page";

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
            StartPreloadAroundCurrentPage();
            return true;
        }

        public bool GoToNextPage()
        {
            if (!CanGoNextPage) return false;

            CurrentPage++;
            StartPreloadAroundCurrentPage();
            return true;
        }

        public async Task DownloadCurrentChapterAsync()
        {
            if (_document == null || IsDownloading) return;

            IsDownloading = true;
            DownloadFeedbackMessage = "正在准备下载本话...";
            DownloadErrorMessage = null;

            try
            {
                var result = await ReaderStorageService.Instance.DownloadChapterAsync(
                    _document,
                    item => DownloadFeedbackMessage = $"{item.Title} · {item.ProgressText}");

                DownloadFeedbackMessage = $"{result.ChapterTitle} 已下载到本地";
                OnPropertyChanged(nameof(CurrentPageUrl));
                OnPropertyChanged(nameof(CurrentPageImage));
            }
            catch (Exception ex)
            {
                DownloadFeedbackMessage = null;
                DownloadErrorMessage = $"下载失败: {ex.Message}";
            }
            finally
            {
                IsDownloading = false;
            }
        }

        public async Task SaveCurrentProgressAsync()
        {
            if (_document == null || CurrentPage <= 0) return;
            await SupabaseService.Instance.SaveReadingProgressAsync(_document, CurrentPage);
        }

        private void StartPreloadAroundCurrentPage()
        {
            if (_document == null || CurrentPage <= 0) return;

            _ = PreloadAroundCurrentPageAsync(_document, CurrentPage);
        }

        private static async Task PreloadAroundCurrentPageAsync(
            ComicReaderDocument document,
            int currentPage)
        {
            try
            {
                await ReaderStorageService.Instance.PreloadAsync(document, currentPage);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[reader-cache] preload failed: {ex.Message}");
            }
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
            OnPropertyChanged(nameof(HasUpscaledAvailable));
            OnPropertyChanged(nameof(IsUpscaledLoaded));
            OnPropertyChanged(nameof(ShowUpscaledQuota));
            OnPropertyChanged(nameof(UpscaledQuotaText));
            OnPropertyChanged(nameof(CanDownloadCurrentChapter));
            OnPropertyChanged(nameof(DownloadButtonText));
            OnPropertyChanged(nameof(HasDownloadFeedback));
            OnPropertyChanged(nameof(HasDownloadError));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
