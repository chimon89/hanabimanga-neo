using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.Services;

namespace hanabimanga.ViewModels
{
    public sealed class UserProfilePageViewModel : INotifyPropertyChanged
    {
        private UserProfileDocument? _document;
        private string _userId = "";
        private bool _isLoading;
        private string? _errorMessage;

        public ObservableCollection<UserTimelineItem> Timeline { get; } = new();

        public UserProfileHeader Profile => _document?.Profile ?? new UserProfileHeader();
        public string TitleBarTitle => Profile.DisplayName;
        public string HeaderTitle => Profile.IsSelf ? "我的主页" : Profile.DisplayName;
        public string DisplayName => Profile.DisplayName;
        public string UsernameText => Profile.UsernameText;
        public string AvatarPreviewUrl => Profile.AvatarPreviewUrl;
        public string BannerPreviewUrl => Profile.BannerPreviewUrl;
        public string JoinedText => Profile.JoinedText;
        public string StatsText => Profile.StatsText;
        public bool IsSelf => Profile.IsSelf;
        public bool HasProfile => _document != null;
        public bool HasTimeline => Timeline.Count > 0;
        public bool HasNoTimeline => !IsLoading && HasProfile && Timeline.Count == 0;
        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading == value) return;
                _isLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasNoTimeline));
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

        public async Task LoadAsync(object? parameter)
        {
            if (!TryResolveUserId(parameter, out var userId))
            {
                ErrorMessage = "缺少用户编号，无法打开主页。";
                return;
            }

            _userId = userId;
            await ReloadAsync();
        }

        public async Task ReloadAsync()
        {
            if (string.IsNullOrWhiteSpace(_userId) || IsLoading) return;

            IsLoading = true;
            ErrorMessage = null;

            try
            {
                _document = await SupabaseService.Instance.GetUserProfilePageAsync(_userId);
                if (_document == null)
                {
                    ErrorMessage = "没有找到这个用户。";
                    Timeline.Clear();
                    RefreshProfileProperties();
                    return;
                }

                Timeline.Clear();
                foreach (var item in _document.Timeline)
                {
                    Timeline.Add(item);
                }

                RefreshProfileProperties();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"用户主页加载失败:{ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private static bool TryResolveUserId(object? parameter, out string userId)
        {
            userId = parameter switch
            {
                string text => text.Trim(),
                Guid guid => guid.ToString(),
                _ => "",
            };

            return !string.IsNullOrWhiteSpace(userId);
        }

        private void RefreshProfileProperties()
        {
            OnPropertyChanged(nameof(Profile));
            OnPropertyChanged(nameof(TitleBarTitle));
            OnPropertyChanged(nameof(HeaderTitle));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(UsernameText));
            OnPropertyChanged(nameof(AvatarPreviewUrl));
            OnPropertyChanged(nameof(BannerPreviewUrl));
            OnPropertyChanged(nameof(JoinedText));
            OnPropertyChanged(nameof(StatsText));
            OnPropertyChanged(nameof(IsSelf));
            OnPropertyChanged(nameof(HasProfile));
            OnPropertyChanged(nameof(HasTimeline));
            OnPropertyChanged(nameof(HasNoTimeline));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
