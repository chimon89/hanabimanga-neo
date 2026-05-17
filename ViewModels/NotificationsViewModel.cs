using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.Services;

namespace hanabimanga.ViewModels
{
    // 通知中心 VM:列表 + 未读数 + 已读操作;Realtime 新通知经 AddRealtime 注入。
    public sealed class NotificationsViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<NotificationItem> Items { get; } = new();

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading == value) return;
                _isLoading = value;
                OnPropertyChanged();
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

        private int _unreadCount;
        public int UnreadCount
        {
            get => _unreadCount;
            private set
            {
                if (_unreadCount == value) return;
                _unreadCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasUnread));
            }
        }

        public bool HasUnread => UnreadCount > 0;
        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public bool IsEmpty => !IsLoading && !HasError && Items.Count == 0;

        public async Task LoadAsync()
        {
            if (IsLoading) return;
            if (!SupabaseService.Instance.IsInitialized ||
                SupabaseService.Instance.CurrentUser is null)
            {
                return;
            }

            IsLoading = true;
            ErrorMessage = null;
            try
            {
                var items = await SupabaseService.Instance.GetNotificationsAsync();
                Items.Clear();
                foreach (var item in items)
                {
                    Items.Add(item);
                }
                RefreshCounts();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"通知加载失败:{ex.Message}";
            }
            finally
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }

        public async Task MarkReadAsync(NotificationItem? item)
        {
            if (item is null || item.IsRead) return;
            item.IsRead = true;
            RefreshCounts();
            try
            {
                await SupabaseService.Instance.MarkNotificationReadAsync(item.Id);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[notifications] mark read failed: {ex.Message}");
            }
        }

        public async Task MarkAllReadAsync()
        {
            if (Items.All(x => x.IsRead)) return;
            foreach (var item in Items)
            {
                item.IsRead = true;
            }
            RefreshCounts();
            try
            {
                await SupabaseService.Instance.MarkAllNotificationsReadAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[notifications] mark all read failed: {ex.Message}");
            }
        }

        // Realtime 新通知:插到列表顶部。
        public void AddRealtime(NotificationItem item)
        {
            if (item is null || Items.Any(x => x.Id == item.Id)) return;
            Items.Insert(0, item);
            RefreshCounts();
            OnPropertyChanged(nameof(IsEmpty));
        }

        public void Clear()
        {
            Items.Clear();
            RefreshCounts();
            ErrorMessage = null;
            OnPropertyChanged(nameof(IsEmpty));
        }

        private void RefreshCounts()
        {
            UnreadCount = Items.Count(x => !x.IsRead);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
