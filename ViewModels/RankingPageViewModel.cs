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
    // 排行页 VM:5 个榜单(日/周/月/评分/评分数)按 SelectorBar 选择懒加载,带每 tab 缓存。
    public sealed class RankingPageViewModel : INotifyPropertyChanged
    {
        private static readonly RankingKind[] Kinds =
        {
            RankingKind.Daily,
            RankingKind.Weekly,
            RankingKind.Monthly,
            RankingKind.Rating,
            RankingKind.RatingCount,
        };

        private readonly List<RankingComicItem>?[] _cache = new List<RankingComicItem>?[Kinds.Length];

        public ObservableCollection<RankingComicItem> Items { get; } = new();

        private int _selectedIndex;
        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (_selectedIndex == value) return;
                _selectedIndex = value;
                OnPropertyChanged();
            }
        }

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

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public bool HasItems => Items.Count > 0;
        public bool IsEmpty => !IsLoading && !HasItems && !HasError;

        public async Task EnsureLoadedAsync(int index)
        {
            if (index < 0 || index >= Kinds.Length) return;
            SelectedIndex = index;

            if (_cache[index] is { } cached)
            {
                ErrorMessage = null;
                ShowItems(cached);
                return;
            }

            await LoadAsync(index);
        }

        public async Task ReloadCurrentAsync()
        {
            var index = SelectedIndex;
            if (index < 0 || index >= Kinds.Length) return;
            _cache[index] = null;
            await LoadAsync(index);
        }

        private async Task LoadAsync(int index)
        {
            IsLoading = true;
            ErrorMessage = null;
            ShowItems(Array.Empty<RankingComicItem>());

            try
            {
                var items = await SupabaseService.Instance.GetRankingComicsAsync(Kinds[index]);
                _cache[index] = items;
                if (SelectedIndex == index)
                {
                    ShowItems(items);
                }
            }
            catch (Exception ex)
            {
                if (SelectedIndex == index)
                {
                    ErrorMessage = $"榜单加载失败:{ex.Message}";
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void ShowItems(IEnumerable<RankingComicItem> items)
        {
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
