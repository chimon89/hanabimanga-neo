using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.Services;

namespace hanabimanga.ViewModels
{
    public sealed class HistoryPageViewModel : INotifyPropertyChanged
    {
        private bool _isLoading;
        private string? _errorMessage;

        public ObservableCollection<ComicListItem> Items { get; } = new();

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading == value) return;
                _isLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(NeedsSignIn));
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

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public bool HasItems => Items.Count > 0;

        public bool IsLoggedIn => SupabaseService.Instance.IsInitialized
            && SupabaseService.Instance.CurrentUser is not null;

        public bool NeedsSignIn => !IsLoading && !IsLoggedIn;

        public bool IsEmpty => !IsLoading && IsLoggedIn && !HasItems && !HasError;

        public string SignInHint => "登录后查看你的阅读历史";

        public string Summary => !IsLoggedIn
            ? string.Empty
            : HasItems ? $"共 {Items.Count} 条阅读记录" : "最近阅读的章节会显示在这里";

        public async Task LoadAsync()
        {
            OnPropertyChanged(nameof(IsLoggedIn));
            OnPropertyChanged(nameof(NeedsSignIn));

            if (!SupabaseService.Instance.IsInitialized)
            {
                ErrorMessage = "Supabase 未初始化:请检查 appsettings.local.json 中的 Url / AnonKey。";
                return;
            }

            if (SupabaseService.Instance.CurrentUser?.Id is not { Length: > 0 })
            {
                Items.Clear();
                ErrorMessage = null;
                RefreshItemProperties();
                OnPropertyChanged(nameof(IsLoggedIn));
                OnPropertyChanged(nameof(NeedsSignIn));
                return;
            }

            IsLoading = true;
            ErrorMessage = null;
            Items.Clear();
            RefreshItemProperties();

            try
            {
                var items = await SupabaseService.Instance.GetReadingHistoryAsync();
                foreach (var item in items)
                    Items.Add(item);
                RefreshItemProperties();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"历史加载失败:{ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void RefreshItemProperties()
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(Summary));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
