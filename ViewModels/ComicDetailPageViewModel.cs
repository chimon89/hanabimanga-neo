using System;
using System.Collections.Generic;
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
        private bool _isInteractionBusy;
        private bool _isFavorite;
        private bool _isLiked;
        private int? _userRating;
        private ComicComment? _randomCommentPreview;
        private string? _errorMessage;
        private string _errorTitle = "加载失败";

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

        public string ErrorTitle
        {
            get => _errorTitle;
            private set
            {
                if (_errorTitle == value) return;
                _errorTitle = value;
                OnPropertyChanged();
            }
        }

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public bool HasDetail => _detail != null;
        public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
        public bool HasTags => TagChips.Count > 0;
        public bool HasChapters => VisibleChapters.Count > 0;
        public bool IsInteractionBusy
        {
            get => _isInteractionBusy;
            private set
            {
                if (_isInteractionBusy == value) return;
                _isInteractionBusy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsInteractionEnabled));
            }
        }
        public bool IsInteractionEnabled => !IsInteractionBusy;

        public bool IsFavorite
        {
            get => _isFavorite;
            private set
            {
                if (_isFavorite == value) return;
                _isFavorite = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FavoriteButtonText));
                OnPropertyChanged(nameof(FavoriteIconGlyph));
            }
        }

        public bool IsLiked
        {
            get => _isLiked;
            private set
            {
                if (_isLiked == value) return;
                _isLiked = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LikeButtonText));
                OnPropertyChanged(nameof(LikeIconGlyph));
            }
        }

        public int? UserRating
        {
            get => _userRating;
            private set
            {
                if (_userRating == value) return;
                _userRating = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RatingButtonText));
            }
        }

        public int? CurrentUserRating => _userRating;

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
        public string FavoriteButtonText => IsFavorite ? "已收藏" : "收藏";
        public string LikeButtonText => IsLiked ? "已点赞" : "点赞";
        public string RatingButtonText => _userRating is { } r ? $"已评 {r} 分" : "评分";
        public string FavoriteIconGlyph => IsFavorite ? "\uE735" : "\uE734";
        public string LikeIconGlyph => IsLiked ? "\uE8E1" : "\uE8E3";
        public bool HasCommentPreview => _randomCommentPreview != null;
        public bool HasNoCommentPreview => _detail != null && _randomCommentPreview == null;
        public string CommentEntryTitle => "评论";
        public string CommentEntryActionText => HasCommentPreview ? "查看全部" : "留下评论";
        public string CommentPreviewContent => _randomCommentPreview?.Content ?? "还没有评论，来留下第一条。";
        public string CommentPreviewAuthorText => _randomCommentPreview is { } comment
            ? $"{comment.DisplayName} · {comment.CreatedAtText}"
            : "在这里留下你对这部漫画的想法";
        public string CommentPreviewMetaText => _randomCommentPreview?.IsSpoiler == true ? "含剧透" : "";
        public bool HasCommentPreviewMeta => !string.IsNullOrWhiteSpace(CommentPreviewMetaText);

        public async Task LoadAsync(string? comicDocumentId)
        {
            if (IsLoading) return;

            if (!SupabaseService.Instance.IsInitialized)
            {
                ErrorTitle = "加载失败";
                ErrorMessage = "Supabase 未初始化:请检查 appsettings.local.json 中的 Url / AnonKey。";
                return;
            }

            if (string.IsNullOrWhiteSpace(comicDocumentId))
            {
                ErrorTitle = "加载失败";
                ErrorMessage = "缺少漫画编号,无法打开详情。";
                return;
            }

            IsLoading = true;
            ErrorTitle = "加载失败";
            ErrorMessage = null;

            try
            {
                _detail = await SupabaseService.Instance.GetComicDetailAsync(comicDocumentId);
                if (_detail == null)
                {
                    ErrorTitle = "加载失败";
                    ErrorMessage = "没有找到这部漫画。";
                    return;
                }

                BuildTagChips();
                BuildChapterCategories();
                await RefreshInteractionStateAsync();
                await RefreshRandomCommentPreviewAsync();
                RefreshDetailProperties();
            }
            catch (Exception ex)
            {
                ErrorTitle = "加载失败";
                ErrorMessage = $"加载漫画详情失败:{ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task ToggleFavoriteAsync()
        {
            if (_detail == null || IsInteractionBusy) return;

            IsInteractionBusy = true;
            ErrorTitle = "收藏失败";
            ErrorMessage = null;
            try
            {
                IsFavorite = await SupabaseService.Instance.SetComicFavoriteAsync(_detail.Id, !IsFavorite);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"收藏操作失败:{ex.Message}";
            }
            finally
            {
                IsInteractionBusy = false;
            }
        }

        public async Task ToggleLikeAsync()
        {
            if (_detail == null || IsInteractionBusy) return;

            IsInteractionBusy = true;
            ErrorTitle = "点赞失败";
            ErrorMessage = null;
            try
            {
                IsLiked = await SupabaseService.Instance.SetComicLikedAsync(_detail.Id, !IsLiked);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"点赞操作失败:{ex.Message}";
            }
            finally
            {
                IsInteractionBusy = false;
            }
        }

        public async Task SubmitRatingAsync(int score)
        {
            if (_detail == null || IsInteractionBusy) return;

            IsInteractionBusy = true;
            ErrorTitle = "评分失败";
            ErrorMessage = null;
            try
            {
                await SupabaseService.Instance.SetComicRatingAsync(_detail.Id, score);
                UserRating = score;

                var (average, count) = await SupabaseService.Instance.GetComicRatingSummaryAsync(_detail.Id);
                _detail.RatingAverage = average;
                _detail.RatingCount = count;
                OnPropertyChanged(nameof(RatingText));
            }
            catch (Exception ex)
            {
                ErrorMessage = $"评分操作失败:{ex.Message}";
            }
            finally
            {
                IsInteractionBusy = false;
            }
        }

        public ComicCommentNavigationParameter? CreateCommentsNavigationParameter()
        {
            return _detail == null
                ? null
                : new ComicCommentNavigationParameter
                {
                    ComicId = _detail.Id,
                    ComicTitle = _detail.Title,
                };
        }

        private async Task RefreshInteractionStateAsync()
        {
            if (_detail == null)
            {
                IsFavorite = false;
                IsLiked = false;
                UserRating = null;
                return;
            }

            var state = await SupabaseService.Instance.TryGetComicInteractionStateAsync(_detail.Id);
            IsFavorite = state.IsFavorite;
            IsLiked = state.IsLiked;
            UserRating = state.UserRating;
        }

        private async Task RefreshRandomCommentPreviewAsync()
        {
            if (_detail == null)
            {
                _randomCommentPreview = null;
                RefreshCommentPreviewProperties();
                return;
            }

            try
            {
                _randomCommentPreview = await SupabaseService.Instance.GetRandomComicCommentAsync(_detail.Id);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[comments] random preview failed: {ex.Message}");
                _randomCommentPreview = null;
            }

            RefreshCommentPreviewProperties();
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

            // 章节总数小于 RangeSize(50)时,默认的 _selectedRangeEnd=50 无法和
            // 实际生成的 range(如 1-10)匹配,导致唯一的标签未选中。统一钳制到
            // 实际章节数范围内,保证默认有且仅有一个标签被选中。
            if (_selectedRangeStart > count || _selectedRangeEnd > count)
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
            OnPropertyChanged(nameof(ErrorTitle));
            OnPropertyChanged(nameof(FavoriteButtonText));
            OnPropertyChanged(nameof(LikeButtonText));
            OnPropertyChanged(nameof(RatingButtonText));
            OnPropertyChanged(nameof(FavoriteIconGlyph));
            OnPropertyChanged(nameof(LikeIconGlyph));
            OnPropertyChanged(nameof(IsInteractionEnabled));
            OnPropertyChanged(nameof(HasSummary));
            OnPropertyChanged(nameof(HasTags));
            OnPropertyChanged(nameof(HasChapters));
            RefreshCommentPreviewProperties();
        }

        private void RefreshCommentPreviewProperties()
        {
            OnPropertyChanged(nameof(HasCommentPreview));
            OnPropertyChanged(nameof(HasNoCommentPreview));
            OnPropertyChanged(nameof(CommentEntryTitle));
            OnPropertyChanged(nameof(CommentEntryActionText));
            OnPropertyChanged(nameof(CommentPreviewContent));
            OnPropertyChanged(nameof(CommentPreviewAuthorText));
            OnPropertyChanged(nameof(CommentPreviewMetaText));
            OnPropertyChanged(nameof(HasCommentPreviewMeta));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
