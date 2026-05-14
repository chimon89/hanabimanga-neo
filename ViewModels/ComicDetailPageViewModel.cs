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
    public sealed class ComicDetailPageViewModel : INotifyPropertyChanged
    {
        private const int RangeSize = 50;
        private readonly List<ComicChapter> _selectedCategoryChapters = new();
        private ComicDetail? _detail;
        private string _selectedCategory = "";
        private int _selectedRangeStart = 1;
        private int _selectedRangeEnd = RangeSize;
        private bool _isDescending;
        private bool _isLoading;
        private string? _errorMessage;

        public ObservableCollection<string> TagChips { get; } = new();
        public ObservableCollection<ChapterCategoryOption> CategoryOptions { get; } = new();
        public ObservableCollection<ChapterRangeOption> RangeOptions { get; } = new();
        public ObservableCollection<ComicChapter> VisibleChapters { get; } = new();

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
        public bool HasDetail => _detail != null;
        public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
        public bool HasTags => TagChips.Count > 0;
        public bool HasChapters => VisibleChapters.Count > 0;

        public string Title => _detail?.Title ?? "漫画详情";
        public string CategoryName => _detail?.CategoryName ?? "漫画";
        public string CoverUrl => _detail?.CoverUrl ?? "";
        public string Summary => _detail?.Summary ?? "暂无简介。";
        public string AuthorsText => BuildAuthorsText();
        public string StatusText => _detail?.IsFinished == true ? "完结" : "连载中";
        public string ChapterCountText => $"{_detail?.Chapters.Count ?? 0} 话";
        public string ChapterSectionTitle => $"章节目录（共{_detail?.Chapters.Count ?? 0}话）";
        public string SelectedCategoryTitle => GetCategoryLabel(_selectedCategory);
        public string SelectedCategoryCountText => $"共 {_selectedCategoryChapters.Count} 话";
        public string SortButtonText => _isDescending ? "倒序" : "正序";
        public string RatingText => BuildRatingText();
        public string ReleaseText => _detail?.ReleaseDate is { } date ? date.ToString("yyyy") : "未知年份";
        public string LatestText => BuildLatestText();

        public async Task LoadAsync(string? comicDocumentId)
        {
            if (IsLoading) return;

            if (!SupabaseService.Instance.IsInitialized)
            {
                ErrorMessage = "Supabase 未初始化:请检查 appsettings.local.json 中的 Url / AnonKey。";
                return;
            }

            if (string.IsNullOrWhiteSpace(comicDocumentId))
            {
                ErrorMessage = "缺少漫画编号,无法打开详情。";
                return;
            }

            IsLoading = true;
            ErrorMessage = null;

            try
            {
                _detail = await SupabaseService.Instance.GetComicDetailAsync(comicDocumentId);
                if (_detail == null)
                {
                    ErrorMessage = "没有找到这部漫画。";
                    return;
                }

                BuildTagChips();
                BuildChapterCategories();
                RefreshDetailProperties();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"加载漫画详情失败:{ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void SelectCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return;

            if (category == _selectedCategory)
            {
                RefreshCategoryOptions();
                return;
            }

            _selectedCategory = category;
            _selectedRangeStart = 1;
            _selectedRangeEnd = RangeSize;

            RefreshChapterCategoryChapters();
            RefreshCategoryOptions();
            RefreshRangeOptions();
            RefreshVisibleChapters();
        }

        public void SelectRange(int start, int end)
        {
            if (start == _selectedRangeStart && end == _selectedRangeEnd)
            {
                RefreshRangeOptions();
                return;
            }

            _selectedRangeStart = start;
            _selectedRangeEnd = end;

            RefreshRangeOptions();
            RefreshVisibleChapters();
        }

        public void ToggleSortDirection()
        {
            _isDescending = !_isDescending;
            RefreshVisibleChapters();
            OnPropertyChanged(nameof(SortButtonText));
        }

        private void BuildTagChips()
        {
            TagChips.Clear();
            if (_detail == null) return;

            var chips = new List<string>
            {
                "漫画",
                StatusText,
            };

            if (!string.IsNullOrWhiteSpace(_detail.CategoryName))
                chips.Add(_detail.CategoryName!);

            if (!string.IsNullOrWhiteSpace(_detail.Region))
                chips.Add(GetRegionLabel(_detail.Region!));

            if (_detail.ReleaseDate is { } releaseDate)
                chips.Add(releaseDate.Year.ToString());

            if (!string.IsNullOrWhiteSpace(_detail.LockStatus))
                chips.Add(GetLockStatusLabel(_detail.LockStatus!));

            if (_detail.HasUpscaled)
                chips.Add("高清");

            chips.AddRange(_detail.Tags);

            foreach (var chip in chips
                         .Where(chip => !string.IsNullOrWhiteSpace(chip))
                         .Select(chip => chip.Trim())
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Take(18))
            {
                TagChips.Add(chip);
            }

            OnPropertyChanged(nameof(HasTags));
        }

        private void BuildChapterCategories()
        {
            var chapters = _detail?.Chapters ?? new List<ComicChapter>();
            var orderedCategories = chapters
                .GroupBy(chapter => chapter.Category)
                .OrderBy(group => GetCategoryOrder(group.Key))
                .ThenBy(group => GetCategoryLabel(group.Key))
                .Select(group => group.Key)
                .ToList();

            _selectedCategory = orderedCategories.FirstOrDefault(category => category == "normal")
                ?? orderedCategories.FirstOrDefault()
                ?? "";
            _selectedRangeStart = 1;
            _selectedRangeEnd = RangeSize;

            RefreshChapterCategoryChapters();
            RefreshCategoryOptions();
            RefreshRangeOptions();
            RefreshVisibleChapters();
        }

        private void RefreshChapterCategoryChapters()
        {
            _selectedCategoryChapters.Clear();
            if (_detail == null || string.IsNullOrWhiteSpace(_selectedCategory)) return;

            _selectedCategoryChapters.AddRange(_detail.Chapters
                .Where(chapter => chapter.Category == _selectedCategory)
                .OrderBy(chapter => chapter.Index)
                .ThenBy(chapter => chapter.Id));

            OnPropertyChanged(nameof(SelectedCategoryTitle));
            OnPropertyChanged(nameof(SelectedCategoryCountText));
        }

        private void RefreshCategoryOptions()
        {
            CategoryOptions.Clear();
            if (_detail == null) return;

            foreach (var group in _detail.Chapters
                         .GroupBy(chapter => chapter.Category)
                         .OrderBy(group => GetCategoryOrder(group.Key))
                         .ThenBy(group => GetCategoryLabel(group.Key)))
            {
                CategoryOptions.Add(new ChapterCategoryOption
                {
                    Key = group.Key,
                    Label = GetCategoryLabel(group.Key),
                    Count = group.Count(),
                    IsSelected = group.Key == _selectedCategory,
                });
            }
        }

        private void RefreshRangeOptions()
        {
            RangeOptions.Clear();
            var count = _selectedCategoryChapters.Count;
            if (count == 0)
            {
                OnPropertyChanged(nameof(HasChapters));
                return;
            }

            if (_selectedRangeStart > count)
            {
                _selectedRangeStart = 1;
                _selectedRangeEnd = Math.Min(RangeSize, count);
            }

            for (var start = 1; start <= count; start += RangeSize)
            {
                var end = Math.Min(start + RangeSize - 1, count);
                RangeOptions.Add(new ChapterRangeOption
                {
                    Start = start,
                    End = end,
                    IsSelected = start == _selectedRangeStart && end == _selectedRangeEnd,
                });
            }
        }

        private void RefreshVisibleChapters()
        {
            VisibleChapters.Clear();

            var visible = _selectedCategoryChapters
                .Skip(Math.Max(_selectedRangeStart - 1, 0))
                .Take(Math.Max(_selectedRangeEnd - _selectedRangeStart + 1, 0));

            if (_isDescending)
                visible = visible.Reverse();

            foreach (var chapter in visible)
                VisibleChapters.Add(chapter);

            OnPropertyChanged(nameof(HasChapters));
            OnPropertyChanged(nameof(SelectedCategoryTitle));
            OnPropertyChanged(nameof(SelectedCategoryCountText));
            OnPropertyChanged(nameof(SortButtonText));
        }

        private string BuildAuthorsText()
        {
            if (_detail?.Authors is { Count: > 0 })
                return string.Join(" / ", _detail.Authors);

            return "作者未知";
        }

        private string BuildRatingText()
        {
            if (_detail == null || _detail.RatingCount <= 0) return "暂无评分";

            return $"{_detail.RatingAverage:0.0} ({_detail.RatingCount})";
        }

        private string BuildLatestText()
        {
            if (_detail == null) return "暂无更新";

            var latest = string.IsNullOrWhiteSpace(_detail.LatestChapterTitle)
                ? "暂无更新"
                : _detail.LatestChapterTitle!;

            if (_detail.LatestChapterUpdatedAt is { } updatedAt)
                return $"{latest} · {updatedAt:yyyy-MM-dd}";

            return latest;
        }

        private static int GetCategoryOrder(string category) => category switch
        {
            "normal" => 0,
            "single" => 1,
            "volume" => 2,
            "special" => 3,
            _ => 9,
        };

        private static string GetCategoryLabel(string category) => category switch
        {
            "normal" => "连载",
            "single" => "单行本",
            "volume" => "单行本",
            "special" => "特典番外",
            "" => "章节",
            _ => category,
        };

        private static string GetRegionLabel(string region) => region switch
        {
            "jp" => "日本",
            "cn" => "国漫",
            "kr" => "韩国",
            _ => region.ToUpperInvariant(),
        };

        private static string GetLockStatusLabel(string lockStatus) => lockStatus switch
        {
            "free" => "免费",
            "vip" => "VIP",
            "locked" => "需解锁",
            _ => lockStatus,
        };

        private void RefreshDetailProperties()
        {
            OnPropertyChanged(nameof(HasDetail));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(CategoryName));
            OnPropertyChanged(nameof(CoverUrl));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(AuthorsText));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ChapterCountText));
            OnPropertyChanged(nameof(ChapterSectionTitle));
            OnPropertyChanged(nameof(SelectedCategoryTitle));
            OnPropertyChanged(nameof(SelectedCategoryCountText));
            OnPropertyChanged(nameof(SortButtonText));
            OnPropertyChanged(nameof(RatingText));
            OnPropertyChanged(nameof(ReleaseText));
            OnPropertyChanged(nameof(LatestText));
            OnPropertyChanged(nameof(HasSummary));
            OnPropertyChanged(nameof(HasTags));
            OnPropertyChanged(nameof(HasChapters));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
