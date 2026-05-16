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

        private static readonly string[] RankingSectionIds =
            { "popular-daily", "popular-weekly", "popular-monthly" };

        private readonly List<HomeFeedItem> _rankingDaily = new();
        private readonly List<HomeFeedItem> _rankingWeekly = new();
        private readonly List<HomeFeedItem> _rankingMonthly = new();

        public ObservableCollection<HomeFeedBanner> Banners { get; } = new();
        public ObservableCollection<HomeFeedItem> RecommendedItems { get; } = new();
        public ObservableCollection<HomeFeedSection> Sections { get; } = new();
        public ObservableCollection<HomeFeedItem> RankingItems { get; } = new();

        public bool HasBanners => Banners.Count > 0;
        public bool HasRecommendations => RecommendedItems.Count > 0;
        public bool CanShuffleRecommendations => _allRecommendedItems.Count > RecommendedPageSize;
        public bool HasRanking => _rankingDaily.Count > 0
            || _rankingWeekly.Count > 0
            || _rankingMonthly.Count > 0;

        private int _selectedRankingIndex;
        public int SelectedRankingIndex
        {
            get => _selectedRankingIndex;
            set
            {
                if (_selectedRankingIndex == value) return;
                _selectedRankingIndex = value;
                OnPropertyChanged();
                RefreshRankingItems();
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
                    SetRankingSections(resp.Sections);

                    foreach (var s in resp.Sections)
                    {
                        if (s.Id == "recommended") continue;
                        if (RankingSectionIds.Contains(s.Id)) continue;
                        Sections.Add(s);
                    }
                }
                else
                {
                    SetRecommendedItems(null);
                    SetRankingSections(null);
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

        private void SetRankingSections(IEnumerable<HomeFeedSection>? sections)
        {
            _rankingDaily.Clear();
            _rankingWeekly.Clear();
            _rankingMonthly.Clear();

            if (sections != null)
            {
                foreach (var s in sections)
                {
                    switch (s.Id)
                    {
                        case "popular-daily":
                            _rankingDaily.AddRange(s.Items);
                            break;
                        case "popular-weekly":
                            _rankingWeekly.AddRange(s.Items);
                            break;
                        case "popular-monthly":
                            _rankingMonthly.AddRange(s.Items);
                            break;
                    }
                }
            }

            _selectedRankingIndex = 0;
            OnPropertyChanged(nameof(SelectedRankingIndex));
            RefreshRankingItems();
            OnPropertyChanged(nameof(HasRanking));
        }

        private void RefreshRankingItems()
        {
            RankingItems.Clear();
            var source = _selectedRankingIndex switch
            {
                1 => _rankingWeekly,
                2 => _rankingMonthly,
                _ => _rankingDaily,
            };
            foreach (var item in source)
            {
                RankingItems.Add(item);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
