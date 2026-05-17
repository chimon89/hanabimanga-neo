using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.Services;

namespace hanabimanga.ViewModels
{
    public sealed class ComicCommentsPageViewModel : INotifyPropertyChanged
    {
        private long _comicId;
        private string _comicTitle = "漫画评论";
        private bool _isLoading;
        private bool _isSubmitting;
        private bool _isSignedIn;
        private string? _errorMessage;
        private string? _feedbackMessage;

        public ObservableCollection<ComicComment> Comments { get; } = new();

        public long ComicId => _comicId;
        public string ComicTitle => _comicTitle;
        public string TitleBarTitle => _comicTitle;
        public string PageTitle => "评论";
        public string PageSubtitle => _comicTitle;
        public bool HasComments => Comments.Count > 0;
        public bool HasNoComments => !IsLoading && Comments.Count == 0 && !HasError;
        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public bool HasFeedback => !string.IsNullOrWhiteSpace(FeedbackMessage);
        public bool IsBusy => IsLoading || IsSubmitting;
        public bool CanSubmit => IsSignedIn && !IsBusy;
        public bool IsSignedIn => _isSignedIn;
        public bool NeedsSignIn => !IsSignedIn;
        public string CommentCountText => Comments.Count == 0 ? "暂无评论" : $"{Comments.Count} 条评论";

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading == value) return;
                _isLoading = value;
                OnPropertyChanged();
                RefreshBusyProperties();
            }
        }

        public bool IsSubmitting
        {
            get => _isSubmitting;
            private set
            {
                if (_isSubmitting == value) return;
                _isSubmitting = value;
                OnPropertyChanged();
                RefreshBusyProperties();
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

        public string? FeedbackMessage
        {
            get => _feedbackMessage;
            private set
            {
                if (_feedbackMessage == value) return;
                _feedbackMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasFeedback));
            }
        }

        public async Task LoadAsync(object? parameter)
        {
            if (!TryApplyParameter(parameter))
            {
                ErrorMessage = "缺少漫画编号，无法打开评论。";
                return;
            }

            RefreshSignInState();
            RefreshHeaderProperties();
            await ReloadAsync();
        }

        public async Task ReloadAsync()
        {
            if (_comicId <= 0 || IsLoading) return;

            RefreshSignInState();
            IsLoading = true;
            ErrorMessage = null;

            try
            {
                var comments = await SupabaseService.Instance.GetComicCommentsAsync(_comicId, limit: 120);
                Comments.Clear();
                foreach (var comment in comments)
                {
                    Comments.Add(comment);
                }

                RefreshCommentProperties();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"评论加载失败:{ex.Message}";
            }
            finally
            {
                IsLoading = false;
                RefreshSignInState();
            }
        }

        public async Task<bool> SubmitCommentAsync(string content, bool isSpoiler)
        {
            if (IsSubmitting) return false;

            var normalized = content.Trim();
            if (normalized.Length < 2)
            {
                ErrorMessage = "评论至少需要 2 个字符。";
                FeedbackMessage = null;
                return false;
            }

            IsSubmitting = true;
            ErrorMessage = null;
            FeedbackMessage = null;

            try
            {
                await SupabaseService.Instance.SubmitComicCommentAsync(_comicId, normalized, isSpoiler);
                FeedbackMessage = "评论已发布，审核结果可能稍后更新可见范围。";
                await ReloadAsync();
                return true;
            }
            catch (Exception ex)
            {
                ErrorMessage = $"评论发布失败:{ex.Message}";
                return false;
            }
            finally
            {
                IsSubmitting = false;
                RefreshSignInState();
            }
        }

        public void RefreshSignInState()
        {
            var signedIn = !string.IsNullOrWhiteSpace(SupabaseService.Instance.CurrentSession?.AccessToken) &&
                !string.IsNullOrWhiteSpace(SupabaseService.Instance.CurrentUser?.Id);
            if (_isSignedIn == signedIn) return;

            _isSignedIn = signedIn;
            OnPropertyChanged(nameof(IsSignedIn));
            OnPropertyChanged(nameof(NeedsSignIn));
            OnPropertyChanged(nameof(CanSubmit));
        }

        private bool TryApplyParameter(object? parameter)
        {
            switch (parameter)
            {
                case ComicCommentNavigationParameter navigation when navigation.ComicId > 0:
                    _comicId = navigation.ComicId;
                    _comicTitle = string.IsNullOrWhiteSpace(navigation.ComicTitle)
                        ? "漫画评论"
                        : navigation.ComicTitle;
                    return true;
                case long comicId when comicId > 0:
                    _comicId = comicId;
                    _comicTitle = "漫画评论";
                    return true;
                case string text when long.TryParse(text, out var comicId) && comicId > 0:
                    _comicId = comicId;
                    _comicTitle = "漫画评论";
                    return true;
                default:
                    return false;
            }
        }

        private void RefreshHeaderProperties()
        {
            OnPropertyChanged(nameof(ComicId));
            OnPropertyChanged(nameof(ComicTitle));
            OnPropertyChanged(nameof(TitleBarTitle));
            OnPropertyChanged(nameof(PageSubtitle));
        }

        private void RefreshCommentProperties()
        {
            OnPropertyChanged(nameof(HasComments));
            OnPropertyChanged(nameof(HasNoComments));
            OnPropertyChanged(nameof(CommentCountText));
        }

        private void RefreshBusyProperties()
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanSubmit));
            OnPropertyChanged(nameof(HasNoComments));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
