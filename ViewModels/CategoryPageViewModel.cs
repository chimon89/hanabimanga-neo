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
    public sealed class CategoryPageViewModel : INotifyPropertyChanged
    {
        private const int PageSize = 24;

        private bool _isLoading;
        private bool _isLoadingMore;
        private bool _hasMore = true;
        private string? _errorMessage;

        // 每次筛选变更自增,使在途的旧请求结果作废,避免筛选切换时的竞态
        private int _generation;

        public ObservableCollection<CategoryOption> Categories { get; } = new();
        public ObservableCollection<ComicListItem> Items { get; } = new();

        public IReadOnlyList<SortOption> Sorts => SortOption.All;
        public IReadOnlyList<RegionOption> Regions => RegionOption.All;
        public IReadOnlyList<StatusOption> Statuses => StatusOption.All;

        public CategoryOption? SelectedCategory { get; set; }
        public SortOption SelectedSort { get; set; } = SortOption.All[0];
        public RegionOption SelectedRegion { get; set; } = RegionOption.All[0];
        public StatusOption SelectedStatus { get; set; } = StatusOption.All[0];

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

        public bool IsLoadingMore
        {
            get => _isLoadingMore;
            private set
            {
                if (_isLoadingMore == value) return;
                _isLoadingMore = value;
                OnPropertyChanged();
            }
        }

        public bool HasMore
        {
            get => _hasMore;
            private set
            {
                if (_hasMore == value) return;
                _hasMore = value;
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
                OnPropertyChanged(nameof(IsEmpty));
            }
        }

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public bool HasItems => Items.Count > 0;
        public bool IsEmpty => !IsLoading && !HasError && Items.Count == 0;

        public async Task InitializeAsync()
        {
            if (!SupabaseService.Instance.IsInitialized)
            {
                ErrorMessage = "Supabase 未初始化:请检查 appsettings.local.json 中的 Url / AnonKey。";
                return;
            }

            if (Categories.Count == 0)
            {
                try
                {
                    var categories = await SupabaseService.Instance.GetCategoriesAsync();
                    foreach (var category in categories)
                        Categories.Add(category);
                }
                catch (Exception ex)
                {
                    ErrorMessage = $"分类加载失败:{ex.Message}";
                    return;
                }
            }

            SelectedCategory ??= Categories.FirstOrDefault();
            await ApplyFiltersAsync();
        }

        public async Task ApplyFiltersAsync()
        {
            if (!SupabaseService.Instance.IsInitialized) return;

            var generation = ++_generation;
            IsLoading = true;
            ErrorMessage = null;
            HasMore = true;
            Items.Clear();
            RefreshItemProperties();

            try
            {
                var page = await FetchPageAsync(offset: 0);
                if (generation != _generation) return;

                foreach (var item in page)
                    Items.Add(item);
                HasMore = page.Count >= PageSize;
            }
            catch (Exception ex)
            {
                if (generation != _generation) return;
                ErrorMessage = $"漫画加载失败:{ex.Message}";
            }
            finally
            {
                if (generation == _generation)
                {
                    IsLoading = false;
                    RefreshItemProperties();
                }
            }
        }

        public async Task LoadMoreAsync()
        {
            if (IsLoading || IsLoadingMore || !HasMore) return;
            if (!SupabaseService.Instance.IsInitialized) return;

            var generation = _generation;
            IsLoadingMore = true;

            try
            {
                var page = await FetchPageAsync(offset: Items.Count);
                if (generation != _generation) return;

                foreach (var item in page)
                    Items.Add(item);
                HasMore = page.Count >= PageSize;
                RefreshItemProperties();
            }
            catch (Exception ex)
            {
                if (generation != _generation) return;
                ErrorMessage = $"加载更多失败:{ex.Message}";
                HasMore = false;
            }
            finally
            {
                IsLoadingMore = false;
            }
        }

        private Task<List<ComicListItem>> FetchPageAsync(int offset)
        {
            return SupabaseService.Instance.GetComicsByFilterAsync(
                categoryId: SelectedCategory?.Id,
                regionFilter: SelectedRegion.Filter,
                isFinished: SelectedStatus.IsFinished,
                orderColumn: SelectedSort.OrderColumn,
                offset: offset,
                limit: PageSize);
        }

        private void RefreshItemProperties()
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
