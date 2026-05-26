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
    public sealed class FeedbackSubmitPageViewModel : INotifyPropertyChanged
    {
        private string? _selectedDomain;
        private string? _selectedCategory;
        private string _title = "";
        private string _description = "";
        private BangumiBook? _selectedBangumiBook;
        private ComicListItem? _selectedComic;
        private bool _isSubmitting;
        private bool _isSearchingBangumi;
        private bool _isSearchingComic;
        private string? _errorMessage;
        private string? _feedbackMessage;
        private string? _bangumiHint;
        private string? _comicHint;
        private string? _quotaText;

        public ObservableCollection<BangumiBook> BangumiResults { get; } = new();
        public ObservableCollection<ComicListItem> ComicResults { get; } = new();

        public string? SelectedDomain
        {
            get => _selectedDomain;
            set
            {
                if (_selectedDomain == value) return;
                _selectedDomain = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsFeatureDomain));
                OnPropertyChanged(nameof(IsResourceDomain));
                OnPropertyChanged(nameof(HasDomain));
                // 切换反馈类型后,原分类与关联项不再适用。
                SelectedCategory = null;
                RefreshSubmitState();
            }
        }

        public string? SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                if (_selectedCategory == value) return;
                _selectedCategory = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCategory));
                OnPropertyChanged(nameof(IsBookRequest));
                OnPropertyChanged(nameof(IsComicAssociation));
                if (!IsBookRequest) SelectedBangumiBook = null;
                if (!IsComicAssociation) SelectedComic = null;
                RefreshSubmitState();
            }
        }

        public string Title
        {
            get => _title;
            set
            {
                var normalized = value ?? "";
                if (_title == normalized) return;
                _title = normalized;
                OnPropertyChanged();
                RefreshSubmitState();
            }
        }

        public string Description
        {
            get => _description;
            set => _description = value ?? "";
        }

        public BangumiBook? SelectedBangumiBook
        {
            get => _selectedBangumiBook;
            set
            {
                if (ReferenceEquals(_selectedBangumiBook, value)) return;
                _selectedBangumiBook = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedBangumiBook));
                OnPropertyChanged(nameof(ShowBangumiResults));
                OnPropertyChanged(nameof(ShowBangumiSearch));
                if (value != null && string.IsNullOrWhiteSpace(_title))
                {
                    Title = $"请求上架:{value.DisplayName}";
                }
                if (value != null)
                {
                    BangumiHint = null;
                }
                RefreshSubmitState();
            }
        }

        public ComicListItem? SelectedComic
        {
            get => _selectedComic;
            set
            {
                if (ReferenceEquals(_selectedComic, value)) return;
                _selectedComic = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedComic));
                OnPropertyChanged(nameof(ShowComicResults));
                OnPropertyChanged(nameof(ShowComicSearch));
                if (value != null)
                {
                    ComicHint = null;
                }
                RefreshSubmitState();
            }
        }

        public bool IsSubmitting
        {
            get => _isSubmitting;
            private set
            {
                if (_isSubmitting == value) return;
                _isSubmitting = value;
                OnPropertyChanged();
                RefreshSubmitState();
            }
        }

        public bool IsSearchingBangumi
        {
            get => _isSearchingBangumi;
            private set
            {
                if (_isSearchingBangumi == value) return;
                _isSearchingBangumi = value;
                OnPropertyChanged();
            }
        }

        public bool IsSearchingComic
        {
            get => _isSearchingComic;
            private set
            {
                if (_isSearchingComic == value) return;
                _isSearchingComic = value;
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

        public string? FeedbackMessage
        {
            get => _feedbackMessage;
            private set
            {
                if (_feedbackMessage == value) return;
                _feedbackMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasFeedback));
            }
        }

        public string? BangumiHint
        {
            get => _bangumiHint;
            private set
            {
                if (_bangumiHint == value) return;
                _bangumiHint = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasBangumiHint));
            }
        }

        public string? ComicHint
        {
            get => _comicHint;
            private set
            {
                if (_comicHint == value) return;
                _comicHint = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasComicHint));
            }
        }

        public string? QuotaText
        {
            get => _quotaText;
            private set
            {
                if (_quotaText == value) return;
                _quotaText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasQuota));
            }
        }

        public bool IsFeatureDomain => _selectedDomain == FeedbackOptions.DomainDev;
        public bool IsResourceDomain => _selectedDomain == FeedbackOptions.DomainOps;
        public bool HasDomain => !string.IsNullOrWhiteSpace(_selectedDomain);
        public bool HasCategory => !string.IsNullOrWhiteSpace(_selectedCategory);
        public bool IsBookRequest => _selectedCategory == FeedbackOptions.CategoryBookRequest;
        public bool IsComicAssociation => FeedbackOptions.RequiresComicAssociation(_selectedCategory);

        public bool HasSelectedBangumiBook => _selectedBangumiBook != null;
        public bool HasSelectedComic => _selectedComic != null;
        public bool ShowBangumiResults => _selectedBangumiBook == null;
        public bool ShowComicResults => _selectedComic == null;
        public bool ShowBangumiSearch => _selectedBangumiBook == null;
        public bool ShowComicSearch => _selectedComic == null;
        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public bool HasFeedback => !string.IsNullOrWhiteSpace(FeedbackMessage);
        public bool HasBangumiHint => !string.IsNullOrWhiteSpace(BangumiHint);
        public bool HasComicHint => !string.IsNullOrWhiteSpace(ComicHint);
        public bool HasQuota => !string.IsNullOrWhiteSpace(QuotaText);

        public IReadOnlyList<string> CurrentCategories => IsFeatureDomain
            ? FeedbackOptions.DevCategories
            : IsResourceDomain
                ? FeedbackOptions.OpsCategories
                : Array.Empty<string>();

        public bool CanSubmit =>
            !IsSubmitting
            && HasDomain
            && HasCategory
            && !string.IsNullOrWhiteSpace(_title.Trim())
            && (!IsBookRequest || _selectedBangumiBook != null);

        public bool CannotSubmit => !CanSubmit;

        public string SubmitHintTitle => CanSubmit ? "可以提交" : "待完善";

        public string SubmitHintText
        {
            get
            {
                if (CanSubmit) return "信息已完整,点击下方按钮提交反馈。";
                if (!HasDomain) return "请先选择反馈类型。";
                if (!HasCategory) return "请选择问题分类。";
                if (IsBookRequest && _selectedBangumiBook == null)
                    return "求书需要先从 Bangumi 搜索并选择条目。";
                if (string.IsNullOrWhiteSpace(_title.Trim()))
                    return "请填写工单标题。";
                return "请完成必填项。";
            }
        }

        public string SubmitButtonText => CanSubmit ? "提交反馈" : "请完成必填项后提交";

        public async Task LoadQuotaAsync()
        {
            try
            {
                var quota = await SupabaseService.Instance.GetTicketQuotaAsync();
                if (quota == null)
                {
                    QuotaText = null;
                    return;
                }

                if (quota.IsVip)
                {
                    QuotaText = "VIP 用户 · 不限反馈提交数量";
                }
                else if (quota.IsBanned)
                {
                    QuotaText = quota.BannedUntil is { } until
                        ? $"提交权限受限,{until.ToLocalTime():MM-dd HH:mm} 后解除"
                        : "提交权限当前受限";
                }
                else if (quota.IsCoolingDown && quota.RetryAfter is { } retry)
                {
                    QuotaText = $"冷却中 · {retry.ToLocalTime():HH:mm} 后可再次提交";
                }
                else
                {
                    QuotaText = $"剩余可提交 {quota.Remaining} 条";
                }
            }
            catch
            {
                QuotaText = null;
            }
        }

        public async Task SearchBangumiAsync(string keyword)
        {
            var trimmed = keyword?.Trim() ?? "";
            if (trimmed.Length == 0)
            {
                BangumiHint = "请输入书名后再搜索。";
                return;
            }

            IsSearchingBangumi = true;
            BangumiHint = null;

            try
            {
                var books = await BangumiService.Instance.SearchBooksAsync(trimmed);
                BangumiResults.Clear();
                foreach (var book in books)
                {
                    BangumiResults.Add(book);
                }

                BangumiHint = books.Count == 0 ? "未在 Bangumi 找到匹配的书籍。" : null;
            }
            catch (Exception ex)
            {
                BangumiHint = $"Bangumi 搜索失败:{ex.Message}";
            }
            finally
            {
                IsSearchingBangumi = false;
            }
        }

        public async Task SearchComicAsync(string keyword)
        {
            var trimmed = keyword?.Trim() ?? "";
            if (trimmed.Length == 0)
            {
                ComicHint = "请输入漫画名后再搜索。";
                return;
            }

            IsSearchingComic = true;
            ComicHint = null;

            try
            {
                var document = await SupabaseService.Instance.SearchComicsAsync(trimmed);
                ComicResults.Clear();
                foreach (var item in document.Items)
                {
                    ComicResults.Add(item);
                }

                ComicHint = document.Items.Count == 0 ? "没有找到匹配的漫画。" : null;
            }
            catch (Exception ex)
            {
                ComicHint = $"漫画搜索失败:{ex.Message}";
            }
            finally
            {
                IsSearchingComic = false;
            }
        }

        public async Task<bool> SubmitAsync()
        {
            if (!CanSubmit) return false;

            IsSubmitting = true;
            ErrorMessage = null;
            FeedbackMessage = null;

            try
            {
                object meta;
                if (IsBookRequest && _selectedBangumiBook is { } book)
                {
                    meta = new Dictionary<string, object?>
                    {
                        ["bangumi_id"] = book.Id,
                        ["bangumi_name"] = book.Name,
                        ["bangumi_name_cn"] = book.NameCn,
                        ["bangumi_cover_url"] = book.CoverUrl,
                    };
                }
                else if (IsComicAssociation && _selectedComic is { } comic)
                {
                    meta = new Dictionary<string, object?>
                    {
                        ["comic_id"] = comic.ComicId,
                        ["comic_title"] = comic.Title,
                        ["comic_subtitle"] = comic.Subtitle,
                        ["comic_cover_url"] = comic.CoverUrl,
                        ["comic_document_id"] = comic.Id,
                    };
                }
                else
                {
                    meta = new Dictionary<string, object?>();
                }

                await SupabaseService.Instance.SubmitTicketAsync(
                    _title.Trim(),
                    Description,
                    _selectedDomain!,
                    _selectedCategory!,
                    meta);

                FeedbackMessage = "反馈已提交,我们会尽快受理。";
                return true;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                return false;
            }
            finally
            {
                IsSubmitting = false;
            }
        }

        private void RefreshSubmitState()
        {
            OnPropertyChanged(nameof(CanSubmit));
            OnPropertyChanged(nameof(CannotSubmit));
            OnPropertyChanged(nameof(SubmitHintTitle));
            OnPropertyChanged(nameof(SubmitHintText));
            OnPropertyChanged(nameof(SubmitButtonText));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
