using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.Services;

namespace hanabimanga.ViewModels
{
    // 收藏 / 点赞列表页的共用 VM 基类。
    // 状态机:
    //   未登录       -> NeedsSignIn = true
    //   加载中       -> IsLoading = true
    //   有数据       -> HasContent = true
    //   登录但为空   -> IsEmpty = true
    //   出错         -> HasError = true
    public abstract class UserListPageViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<BookshelfComicItem> Items { get; } = new();

        public bool IsLoggedIn => SupabaseService.Instance.IsInitialized
            && SupabaseService.Instance.CurrentUser is not null;

        public bool NeedsSignIn => !IsLoading && !IsLoggedIn;

        public bool IsEmpty => !IsLoading && IsLoggedIn && !HasError && Items.Count == 0;

        public bool HasContent => Items.Count > 0;

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading == value) return;
                _isLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NeedsSignIn));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }

        private string? _errorMessage;
        public string? ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (_errorMessage == value) return;
                _errorMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }

        public bool HasError => !string.IsNullOrEmpty(_errorMessage);

        public async Task LoadAsync()
        {
            if (IsLoading) return;

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
                OnPropertyChanged(nameof(HasContent));
                OnPropertyChanged(nameof(IsEmpty));
                return;
            }

            IsLoading = true;
            ErrorMessage = null;
            try
            {
                var doc = await SupabaseService.Instance.GetBookshelfAsync();
                Items.Clear();
                foreach (var item in PickList(doc))
                {
                    Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"加载失败:{ex.Message}";
            }
            finally
            {
                IsLoading = false;
                OnPropertyChanged(nameof(HasContent));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }

        protected abstract IEnumerable<BookshelfComicItem> PickList(BookshelfDocument doc);

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
