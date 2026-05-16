using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.Services;

namespace hanabimanga.ViewModels
{
    public sealed class UserSettingsPageViewModel : INotifyPropertyChanged
    {
        private bool _isLoading;
        private bool _isSaving;
        private string? _errorMessage;
        private string? _feedbackMessage;
        private UserSettingsDocument? _document;

        public ObservableCollection<UserBadgeItem> Badges { get; } = new();
        public ObservableCollection<AvatarPresetOption> AvatarPresets { get; } = new();

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

        public bool IsSaving
        {
            get => _isSaving;
            private set
            {
                if (_isSaving == value) return;
                _isSaving = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanSubmit));
            }
        }

        public bool IsBusy => IsLoading || IsSaving;
        public bool CanSubmit => !IsBusy;
        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public bool HasFeedback => !string.IsNullOrWhiteSpace(FeedbackMessage);
        public bool HasBadges => Badges.Count > 0;
        public bool HasNoBadges => !IsLoading && Badges.Count == 0 && !HasError;

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

        public string Email => _document?.Email ?? "";
        public string Username => _document?.Profile.Username ?? "";
        public string DisplayName => _document?.Profile.DisplayName ?? "";
        public string CurrentAvatarPreviewUrl => ToAvatarPreviewUrl(_document?.Profile.AvatarUrl);
        public bool IsVip => _document?.Profile.VipExpirationDate is { } expiresAt && expiresAt > DateTime.UtcNow;
        public bool CanUploadCustomAvatar => IsVip;
        public string CustomAvatarHint => IsVip
            ? "选择 WebP 图片，最大 2 MB"
            : "自定义头像仅 VIP 可上传";

        public async Task LoadAsync()
        {
            if (IsLoading) return;

            IsLoading = true;
            ErrorMessage = null;
            FeedbackMessage = null;

            try
            {
                _document = await SupabaseService.Instance.GetCurrentUserSettingsAsync();
                RefreshCollections();
                RefreshProfileProperties();
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

        public async Task SaveProfileAsync(string username, string displayName)
        {
            if (IsSaving) return;

            IsSaving = true;
            ErrorMessage = null;
            FeedbackMessage = null;

            try
            {
                _document!.Profile = await SupabaseService.Instance.UpdateCurrentUserProfileAsync(username, displayName);
                FeedbackMessage = "资料已保存。";
                RefreshProfileProperties();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsSaving = false;
            }
        }

        public async Task SelectPresetAvatarAsync(string fileName)
        {
            if (IsSaving || string.IsNullOrWhiteSpace(fileName)) return;

            IsSaving = true;
            ErrorMessage = null;
            FeedbackMessage = null;

            try
            {
                _document!.Profile = await SupabaseService.Instance.SetCurrentUserAvatarAsync(fileName);
                FeedbackMessage = "头像已更新。";
                RefreshProfileProperties();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsSaving = false;
            }
        }

        public async Task UploadCustomAvatarAsync(string filePath)
        {
            if (IsSaving || string.IsNullOrWhiteSpace(filePath)) return;

            IsSaving = true;
            ErrorMessage = null;
            FeedbackMessage = null;

            try
            {
                var avatarUrl = await SupabaseService.Instance.UploadCurrentUserAvatarAsync(filePath);
                _document!.Profile.AvatarUrl = avatarUrl;
                FeedbackMessage = "自定义头像已上传。";
                RefreshProfileProperties();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsSaving = false;
            }
        }

        public async Task ToggleBadgeDisplayAsync(long userBadgeId)
        {
            if (IsSaving) return;

            var badge = FindBadge(userBadgeId);
            if (badge == null || badge.IsExpired) return;

            IsSaving = true;
            ErrorMessage = null;
            FeedbackMessage = null;

            try
            {
                var next = !badge.IsDisplayed;
                await SupabaseService.Instance.SetCurrentUserBadgeDisplayedAsync(userBadgeId, next);
                badge.IsDisplayed = next;
                FeedbackMessage = next ? "徽章已设为展示。" : "徽章已取消展示。";
                RefreshCollections();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsSaving = false;
            }
        }

        public async Task UpdateEmailAsync(string email)
        {
            if (IsSaving) return;

            IsSaving = true;
            ErrorMessage = null;
            FeedbackMessage = null;

            try
            {
                await SupabaseService.Instance.UpdateCurrentUserEmailAsync(email);
                FeedbackMessage = "邮箱变更请求已提交，请按邮件提示完成确认。";
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsSaving = false;
            }
        }

        public async Task UpdatePasswordAsync(string password, string confirmation)
        {
            if (IsSaving) return;

            if (password != confirmation)
            {
                ErrorMessage = "两次输入的新密码不一致。";
                return;
            }

            IsSaving = true;
            ErrorMessage = null;
            FeedbackMessage = null;

            try
            {
                await SupabaseService.Instance.UpdateCurrentUserPasswordAsync(password);
                FeedbackMessage = "密码已更新。";
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsSaving = false;
            }
        }

        private UserBadgeItem? FindBadge(long id)
        {
            foreach (var badge in Badges)
            {
                if (badge.Id == id) return badge;
            }

            return null;
        }

        private void RefreshCollections()
        {
            Badges.Clear();
            AvatarPresets.Clear();

            if (_document != null)
            {
                foreach (var badge in _document.Badges)
                {
                    Badges.Add(badge);
                }

                foreach (var preset in _document.AvatarPresets)
                {
                    AvatarPresets.Add(preset);
                }
            }

            OnPropertyChanged(nameof(HasBadges));
            OnPropertyChanged(nameof(HasNoBadges));
        }

        private void RefreshProfileProperties()
        {
            OnPropertyChanged(nameof(Email));
            OnPropertyChanged(nameof(Username));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(CurrentAvatarPreviewUrl));
            OnPropertyChanged(nameof(IsVip));
            OnPropertyChanged(nameof(CanUploadCustomAvatar));
            OnPropertyChanged(nameof(CustomAvatarHint));
        }

        private static string ToAvatarPreviewUrl(string? avatarUrl)
        {
            if (string.IsNullOrWhiteSpace(avatarUrl))
            {
                return "ms-appx:///Assets/avatar/ic_avatar_default.webp";
            }

            if (Uri.TryCreate(avatarUrl, UriKind.Absolute, out _))
            {
                return avatarUrl;
            }

            var fileName = Path.GetFileName(avatarUrl.Trim().Replace('\\', '/'));
            return string.IsNullOrWhiteSpace(fileName)
                ? "ms-appx:///Assets/avatar/ic_avatar_default.webp"
                : $"ms-appx:///Assets/avatar/{fileName}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
