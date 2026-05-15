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
    public class HomePageViewModel : INotifyPropertyChanged
    {
        private const int RecommendedPageSize = 4;
        private readonly List<HomeFeedItem> _allRecommendedItems = new();
        private int _recommendedStartIndex;

        public ObservableCollection<HomeFeedBanner> Banners { get; } = new();
        public ObservableCollection<HomeFeedItem> RecommendedItems { get; } = new();
        public ObservableCollection<HomeFeedSection> Sections { get; } = new();

        public bool HasBanners => Banners.Count > 0;
        public bool HasRecommendations => RecommendedItems.Count > 0;
        public bool CanShuffleRecommendations => _allRecommendedItems.Count > RecommendedPageSize;

        private bool _isLoading;
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
            }
        }

        public bool HasError => !string.IsNullOrEmpty(_errorMessage);

        public async Task LoadAsync()
        {
            if (IsLoading) return;

            if (!SupabaseService.Instance.IsInitialized)
            {
                ErrorMessage = "Supabase 未初始化:请检查 appsettings.local.json 中的 Url / AnonKey。";
                return;
            }

            IsLoading = true;
            ErrorMessage = null;
            try
            {
                var resp = await SupabaseService.Instance.GetHomeFeedAsync();

                Banners.Clear();
                if (resp?.Banners != null)
                {
                    foreach (var b in resp.Banners)
                        Banners.Add(b);
                }
                OnPropertyChanged(nameof(HasBanners));

                Sections.Clear();
                if (resp?.Sections != null)
                {
                    var recommendedSection = resp.Sections.FirstOrDefault(s => s.Id == "recommended");
                    SetRecommendedItems(recommendedSection?.Items);

                    foreach (var s in resp.Sections)
                    {
                        if (s.Id == "recommended") continue;
                        Sections.Add(s);
                    }
                }
                else
                {
                    SetRecommendedItems(null);
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"加载失败:{ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void ShowNextRecommendations()
        {
            if (!CanShuffleRecommendations) return;

            _recommendedStartIndex = (_recommendedStartIndex + RecommendedPageSize) % _allRecommendedItems.Count;
            RefreshRecommendedItems();
        }

        private void SetRecommendedItems(IReadOnlyList<HomeFeedItem>? items)
        {
            _allRecommendedItems.Clear();
            if (items != null)
            {
                _allRecommendedItems.AddRange(items);
            }

            _recommendedStartIndex = 0;
            RefreshRecommendedItems();
            OnPropertyChanged(nameof(CanShuffleRecommendations));
        }

        private void RefreshRecommendedItems()
        {
            RecommendedItems.Clear();
            if (_allRecommendedItems.Count == 0)
            {
                OnPropertyChanged(nameof(HasRecommendations));
                return;
            }

            var count = Math.Min(RecommendedPageSize, _allRecommendedItems.Count);
            for (var i = 0; i < count; i++)
            {
                var index = (_recommendedStartIndex + i) % _allRecommendedItems.Count;
                RecommendedItems.Add(_allRecommendedItems[index]);
            }

            OnPropertyChanged(nameof(HasRecommendations));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
