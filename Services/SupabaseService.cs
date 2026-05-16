using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using hanabimanga.Models;
using Newtonsoft.Json;
using Supabase;
using Supabase.Functions;
using Supabase.Gotrue;
using Client = Supabase.Client;

namespace hanabimanga.Services
{
    public sealed class SupabaseService
    {
        private static readonly Lazy<SupabaseService> _instance =
            new(() => new SupabaseService());

        public static SupabaseService Instance => _instance.Value;

        private Client? _client;
        private string? _supabaseUrl;
        private string? _supabaseAnonKey;
        private string? _readerClientVerifySecret;
        private string? _readerClientFingerprint;
        private readonly HttpClient _httpClient = new();
        private readonly SemaphoreSlim _initLock = new(1, 1);

        public Client Client =>
            _client ?? throw new InvalidOperationException(
                "SupabaseService 尚未初始化,请先调用 InitializeAsync。");

        public Session? CurrentSession => _client?.Auth.CurrentSession;
        public User? CurrentUser => _client?.Auth.CurrentUser;
        public bool IsInitialized => _client is not null;

        private SupabaseService() { }

        public async Task InitializeAsync(
            string url,
            string anonKey,
            SupabaseOptions? options = null,
            string? readerClientVerifySecret = null,
            string? readerClientFingerprint = null)
        {
            await _initLock.WaitAsync();
            try
            {
                if (_client is not null) return;

                options ??= new SupabaseOptions
                {
                    AutoConnectRealtime = true,
                    AutoRefreshToken = true,
                };

                // 注入文件持久化:Client.InitializeAsync 会自动 LoadSession 还原,
                // 之后由 AutoRefreshToken 配合 SDK 内部 TokenRefresh 定时器自动续期
                // (access_token 1h / refresh_token 90d)。
                options.SessionHandler = new FileSessionPersistence();

                var client = new Client(url, anonKey, options);
                await client.InitializeAsync();

                // 从磁盘还原 CurrentSession。InitializeAsync 内部不会自动 LoadSession,
                // 必须显式调一次,才能让后续 RetrieveSessionAsync 有 Session 可刷。
                client.Auth.LoadSession();

                // 启动时主动刷新:若磁盘上的 access_token 已过期,立即用 refresh_token 换新;
                // 若 refresh_token 也失效,SDK 会把用户登出。
                try
                {
                    await client.Auth.RetrieveSessionAsync();
                    Debug.WriteLine(
                        client.Auth.CurrentSession is { } s
                            ? $"[supabase] session restored, user={client.Auth.CurrentUser?.Email}, expires={s.ExpiresAt():O}"
                            : "[supabase] no valid session after retrieve");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[supabase] RetrieveSessionAsync failed: {ex.Message}");
                }

                _client = client;
                _supabaseUrl = url.TrimEnd('/');
                _supabaseAnonKey = anonKey;
                _readerClientVerifySecret = readerClientVerifySecret;
                _readerClientFingerprint = readerClientFingerprint;
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task<HomeFeedResponse?> GetHomeFeedAsync()
        {
            var raw = await Client.Functions.Invoke("home-feed");
            Debug.WriteLine($"[home-feed] raw HTTP body:\n{raw}");

            if (string.IsNullOrWhiteSpace(raw)) return null;

            var rawResp = JsonConvert.DeserializeObject<RawHomeFeed>(raw);
            if (rawResp?.Data == null)
            {
                Debug.WriteLine("[home-feed] parsed: data 为 null");
                return null;
            }

            var result = MapHomeFeed(rawResp.Data);
            Debug.WriteLine(
                $"[home-feed] mapped: banners={result.Banners.Count}, " +
                $"sections={result.Sections.Count} " +
                $"[{string.Join(",", result.Sections.Select(s => $"{s.Id}:{s.Items.Count}"))}]");
            return result;
        }

        public async Task<ComicDetail?> GetComicDetailAsync(string comicDocumentId)
        {
            if (string.IsNullOrWhiteSpace(comicDocumentId)) return null;

            var comic = long.TryParse(comicDocumentId, out var comicId)
                ? await GetComicRecordByIdAsync(comicId)
                : await GetComicRecordBySlugAsync(comicDocumentId.Trim());

            if (comic == null) return null;

            var categoryTask = GetCategoryRecordAsync(comic.CategoryId);
            var tagsTask = GetComicTagRecordsAsync(comic.Id);
            var chaptersTask = GetChapterRecordsAsync(comic.Id);

            await Task.WhenAll(categoryTask, tagsTask, chaptersTask);

            var category = categoryTask.Result;
            var tagNames = tagsTask.Result
                .Select(t => t.Tag?.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var chapters = chaptersTask.Result
                .Select(chapter => new ComicChapter
                {
                    Id = chapter.Id,
                    ComicId = comic.Id,
                    Title = string.IsNullOrWhiteSpace(chapter.Title) ? $"第{chapter.Index ?? 0}话" : chapter.Title!,
                    Index = chapter.Index ?? 0,
                    Category = string.IsNullOrWhiteSpace(chapter.Category) ? "normal" : chapter.Category!,
                    ChapterFolder = chapter.ChapterFolder,
                    ImageCount = chapter.ImageCount ?? 0,
                    ImageFormat = chapter.ImageFormat,
                    UpdatedAt = chapter.UpdatedAt,
                })
                .OrderBy(chapter => chapter.Category == "normal" ? 0 : 1)
                .ThenBy(chapter => chapter.Index)
                .ThenBy(chapter => chapter.Id)
                .ToList();

            return new ComicDetail
            {
                Id = comic.Id,
                Title = comic.Title ?? "",
                Slug = comic.Slug,
                Summary = string.IsNullOrWhiteSpace(comic.Summary) ? comic.Note : comic.Summary,
                CoverUrl = comic.CoverUrl,
                PosterUrl = comic.PosterUrl,
                Authors = comic.Authors ?? new List<string>(),
                Region = comic.Region,
                CategoryName = category?.Name,
                LockStatus = comic.LockStatus,
                IsFinished = comic.IsFinished,
                HasUpscaled = comic.HasUpscaled,
                ReleaseDate = comic.ReleaseDate,
                RatingAverage = comic.RatingAverage ?? 0,
                RatingCount = comic.RatingCount ?? 0,
                ViewCount = comic.ViewCount ?? 0,
                LatestChapterTitle = comic.LatestChapterTitle,
                LatestChapterUpdatedAt = comic.LatestChapterUpdatedAt,
                Tags = tagNames,
                Chapters = chapters,
            };
        }

        public async Task<ComicReaderDocument> GetComicReaderAsync(
            long comicId,
            long chapterId,
            bool useUpscaled = false)
        {
            var comic = await GetComicRecordByIdAsync(comicId)
                ?? throw new InvalidOperationException("没有找到这部漫画。");

            var chapters = (await GetChapterRecordsAsync(comicId))
                .Select(chapter => new ComicChapter
                {
                    ComicId = comicId,
                    Id = chapter.Id,
                    Title = string.IsNullOrWhiteSpace(chapter.Title) ? $"第{chapter.Index ?? 0}话" : chapter.Title!,
                    Index = chapter.Index ?? 0,
                    Category = string.IsNullOrWhiteSpace(chapter.Category) ? "normal" : chapter.Category!,
                    ChapterFolder = chapter.ChapterFolder,
                    ImageCount = chapter.ImageCount ?? 0,
                    ImageFormat = chapter.ImageFormat,
                    UpdatedAt = chapter.UpdatedAt,
                })
                .OrderBy(chapter => GetChapterCategoryOrder(chapter.Category))
                .ThenBy(chapter => chapter.Index)
                .ThenBy(chapter => chapter.Id)
                .ToList();

            var chapterIndex = chapters.FindIndex(chapter => chapter.Id == chapterId);
            if (chapterIndex < 0)
            {
                throw new InvalidOperationException("没有找到这个章节。");
            }

            var selectedChapter = chapters[chapterIndex];
            if (selectedChapter.ImageCount <= 0)
            {
                throw new InvalidOperationException("这个章节暂无图片。");
            }

            // 漫画未提供超分版本时,即使用户要求 vip 也回退到 sd
            var hasUpscaled = comic.HasUpscaled;
            var effectiveUpscaled = useUpscaled && hasUpscaled;

            var imageResponse = await InvokeReaderImageUrlAsync(
                comicId,
                chapterId,
                selectedChapter.ImageCount,
                selectedChapter.ImageFormat,
                effectiveUpscaled);

            var pageImages = imageResponse.Urls?
                .Where(item => !string.IsNullOrWhiteSpace(item.Url))
                .Select(item => new ReaderPageImage
                {
                    PageNumber = TryParsePageNumber(item.Page),
                    Url = item.Url!,
                })
                .Where(item => item.PageNumber > 0)
                .OrderBy(item => item.PageNumber)
                .ToList() ?? new List<ReaderPageImage>();

            return new ComicReaderDocument
            {
                ComicId = comicId,
                ComicTitle = comic.Title ?? "",
                Chapter = selectedChapter,
                PreviousChapter = chapterIndex > 0 ? chapters[chapterIndex - 1] : null,
                NextChapter = chapterIndex < chapters.Count - 1 ? chapters[chapterIndex + 1] : null,
                TotalChapters = chapters.Count,
                Pages = pageImages,
                PlatformRouted = imageResponse.Metadata?.PlatformRouted ?? "",
                HasUpscaled = hasUpscaled,
                IsUpscaled = effectiveUpscaled,
                Quota = imageResponse.Quota is { } quota
                    ? new ImageQuota
                    {
                        UsedToday = quota.UsedToday,
                        Remaining = quota.Remaining,
                        DailyLimit = quota.DailyLimit,
                        IsVip = quota.IsVip,
                    }
                    : null,
            };
        }

        public async Task<bool> ShouldAutoUseUpscaledAsync(long comicId)
        {
            if (string.IsNullOrWhiteSpace(CurrentSession?.AccessToken))
            {
                return false;
            }

            var comic = await GetComicRecordByIdAsync(comicId);
            if (comic?.HasUpscaled != true)
            {
                return false;
            }

            var quota = await GetPremiumQuotaAsync();
            return quota?.IsVip == true;
        }

        public async Task SignInAsync(string email, string password)
        {
            await Client.Auth.SignInWithPassword(email, password);
        }

        public async Task SignUpAsync(string email, string password)
        {
            await Client.Auth.SignUp(email, password);
        }

        public async Task SendMagicLinkAsync(
            string email,
            string redirectTo = "https://hanabimanga.top/auth/callback")
        {
            await Client.Auth.SignInWithOtp(new SignInWithPasswordlessEmailOptions(email)
            {
                EmailRedirectTo = redirectTo,
                ShouldCreateUser = true,
            });
        }

        public async Task SignOutAsync()
        {
            await Client.Auth.SignOut(Constants.SignOutScope.Local);
        }

        public async Task<Announcement?> GetAnnouncementAsync(string announcementId)
        {
            if (string.IsNullOrWhiteSpace(announcementId)) return null;

            var response = await Client
                .From<Announcement>()
                .Where(announcement => announcement.Id == announcementId)
                .Limit(1)
                .Get();

            var announcement = response.Models.FirstOrDefault();
            Debug.WriteLine(
                announcement != null
                    ? $"[announcements] hit id={announcement.Id}, title={announcement.Title}"
                    : $"[announcements] no record for id={announcementId}");
            return announcement;
        }

        public async Task<UserProfile?> GetCurrentUserProfileAsync(string userId)
        {
            var response = await Client
                .From<UserProfile>()
                .Where(profile => profile.Id == userId)
                .Limit(1)
                .Get();

            var profile = response.Models.FirstOrDefault();
            Debug.WriteLine(
                "[profiles] current user profile: " +
                $"count={response.Models.Count}, " +
                $"id={profile?.Id ?? "(null)"}, " +
                $"username={profile?.Username ?? "(null)"}, " +
                $"display_name={profile?.DisplayName ?? "(null)"}, " +
                $"avatar_url={profile?.AvatarUrl ?? "(null)"}, " +
                $"vip_expiration_date={profile?.VipExpirationDate?.ToString("O") ?? "(null)"}");

            return profile;
        }

        public async Task<UserSettingsDocument> GetCurrentUserSettingsAsync()
        {
            var userId = GetRequiredCurrentUserId("请先登录后再查看账户设置。");
            var profileTask = GetCurrentUserProfileAsync(userId);
            var badgesTask = GetCurrentUserBadgesAsync(userId);

            await Task.WhenAll(profileTask, badgesTask);

            return new UserSettingsDocument
            {
                Email = CurrentUser?.Email ?? "",
                Profile = profileTask.Result ?? new UserProfile { Id = userId },
                Badges = badgesTask.Result,
                AvatarPresets = BuildAvatarPresetOptions(),
            };
        }

        public async Task<UserProfile> UpdateCurrentUserProfileAsync(string username, string displayName)
        {
            var userId = GetRequiredCurrentUserId("请先登录后再修改资料。");
            var normalizedUsername = NormalizeOptionalProfileText(username, 32);
            var normalizedDisplayName = NormalizeOptionalProfileText(displayName, 32);

            if (normalizedUsername is { Length: < 2 })
            {
                throw new InvalidOperationException("用户名至少需要 2 个字符。");
            }

            await PatchCurrentUserProfileFieldsAsync(userId, new Dictionary<string, object?>
            {
                ["username"] = normalizedUsername,
                ["display_name"] = normalizedDisplayName,
            });

            return await GetCurrentUserProfileAsync(userId) ?? new UserProfile { Id = userId };
        }

        public async Task<UserProfile> SetCurrentUserAvatarAsync(string avatarValue)
        {
            var userId = GetRequiredCurrentUserId("请先登录后再修改头像。");
            var normalizedAvatar = NormalizeOptionalProfileText(avatarValue, 512)
                ?? "ic_avatar_default.webp";

            await PatchCurrentUserProfileFieldsAsync(userId, new Dictionary<string, object?>
            {
                ["avatar_url"] = normalizedAvatar,
            });

            return await GetCurrentUserProfileAsync(userId) ?? new UserProfile { Id = userId };
        }

        public async Task<string> UploadCurrentUserAvatarAsync(string localFilePath)
        {
            var userId = GetRequiredCurrentUserId("请先登录后再上传头像。");
            if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
            {
                throw new InvalidOperationException("没有找到要上传的头像文件。");
            }

            var profile = await GetCurrentUserProfileAsync(userId);
            if (profile?.VipExpirationDate is not { } vipExpiration ||
                vipExpiration <= DateTime.UtcNow)
            {
                throw new InvalidOperationException("自定义头像仅 VIP 用户可上传。");
            }

            var fileInfo = new FileInfo(localFilePath);
            if (!string.Equals(fileInfo.Extension, ".webp", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("自定义头像目前仅支持 WebP 文件。");
            }

            if (fileInfo.Length > 2 * 1024 * 1024)
            {
                throw new InvalidOperationException("头像文件不能超过 2 MB。");
            }

            var bytes = await File.ReadAllBytesAsync(localFilePath);
            var objectName = $"{userId}.webp";
            await UploadStorageObjectAsync("avatars", objectName, bytes, "image/webp");

            var publicUrl = $"{_supabaseUrl}/storage/v1/object/public/avatars/{Uri.EscapeDataString(objectName)}" +
                $"?t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            await SetCurrentUserAvatarAsync(publicUrl);
            return publicUrl;
        }

        public async Task SetCurrentUserBadgeDisplayedAsync(long userBadgeId, bool isDisplayed)
        {
            var userId = GetRequiredCurrentUserId("请先登录后再编辑徽章。");
            if (userBadgeId <= 0)
            {
                throw new InvalidOperationException("徽章参数无效。");
            }

            await PatchRestRecordAsync(
                $"user_badges?id=eq.{userBadgeId}&user_id=eq.{Uri.EscapeDataString(userId)}",
                new { is_displayed = isDisplayed });
        }

        public async Task UpdateCurrentUserEmailAsync(string email)
        {
            var normalized = email.Trim();
            if (string.IsNullOrWhiteSpace(normalized) ||
                !normalized.Contains('@') ||
                normalized.StartsWith("@", StringComparison.Ordinal) ||
                normalized.EndsWith("@", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("请输入有效的邮箱地址。");
            }

            GetRequiredCurrentUserId("请先登录后再变更邮箱。");
            await Client.Auth.Update(new UserAttributes { Email = normalized });
        }

        public async Task UpdateCurrentUserPasswordAsync(string password)
        {
            if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            {
                throw new InvalidOperationException("新密码至少需要 6 个字符。");
            }

            GetRequiredCurrentUserId("请先登录后再修改密码。");
            await Client.Auth.Update(new UserAttributes { Password = password });
        }

        public async Task<ComicInteractionState> GetComicInteractionStateAsync(long comicId)
        {
            var userId = GetRequiredCurrentUserId("请先登录后再同步收藏和点赞状态。");
            var favoriteTask = HasInteractionAsync("comics_favorites", userId, comicId);
            var likeTask = HasInteractionAsync("comic_likes", userId, comicId);
            var ratingTask = GetUserComicRatingAsync(userId, comicId);

            await Task.WhenAll(favoriteTask, likeTask, ratingTask);

            return new ComicInteractionState
            {
                IsFavorite = favoriteTask.Result,
                IsLiked = likeTask.Result,
                UserRating = ratingTask.Result,
            };
        }

        public async Task<ComicInteractionState> TryGetComicInteractionStateAsync(long comicId)
        {
            if (string.IsNullOrWhiteSpace(CurrentSession?.AccessToken) ||
                string.IsNullOrWhiteSpace(CurrentUser?.Id))
            {
                return new ComicInteractionState();
            }

            return await GetComicInteractionStateAsync(comicId);
        }

        public async Task<bool> SetComicFavoriteAsync(long comicId, bool isFavorite)
        {
            var userId = GetRequiredCurrentUserId("请先登录后再收藏漫画。");
            await SetInteractionAsync("comics_favorites", userId, comicId, isFavorite);
            return isFavorite;
        }

        public async Task<bool> SetComicLikedAsync(long comicId, bool isLiked)
        {
            var userId = GetRequiredCurrentUserId("请先登录后再点赞漫画。");
            await SetInteractionAsync("comic_likes", userId, comicId, isLiked);
            return isLiked;
        }

        public async Task<int> SetComicRatingAsync(long comicId, int score)
        {
            if (score < 1 || score > 10)
            {
                throw new ArgumentOutOfRangeException(nameof(score), "评分必须在 1-10 之间。");
            }

            var userId = GetRequiredCurrentUserId("请先登录后再评分。");
            var exists = await HasInteractionAsync("comic_ratings", userId, comicId);
            if (exists)
            {
                await PatchRestRecordAsync(
                    $"comic_ratings?user_id=eq.{Uri.EscapeDataString(userId)}&comic_id=eq.{comicId}",
                    new { score, updated_at = DateTime.UtcNow });
            }
            else
            {
                await PostRestRecordAsync("comic_ratings", new
                {
                    user_id = userId,
                    comic_id = comicId,
                    score,
                });
            }

            return score;
        }

        public async Task<(double Average, int Count)> GetComicRatingSummaryAsync(long comicId)
        {
            var stats = await GetSingleRestRecordAsync<RawComicRatingStats>(
                "comics", "rating_average,rating_count", $"id=eq.{comicId}");
            return (stats?.RatingAverage ?? 0, stats?.RatingCount ?? 0);
        }

        private async Task<int?> GetUserComicRatingAsync(string userId, long comicId)
        {
            var rows = await GetAuthenticatedRestRecordsAsync<RawComicRatingRecord>(
                $"comic_ratings?select=score&user_id=eq.{Uri.EscapeDataString(userId)}&comic_id=eq.{comicId}&limit=1");
            return rows.Count > 0 ? rows[0].Score : null;
        }

        private async Task<List<UserBadgeItem>> GetCurrentUserBadgesAsync(string userId)
        {
            var rows = await GetAuthenticatedRestRecordsAsync<RawUserBadgeRecord>(
                "user_badges?select=id,badge_id,is_displayed,expires_at" +
                $"&user_id=eq.{Uri.EscapeDataString(userId)}&order=created_at.desc&limit=200");

            if (rows.Count == 0)
            {
                return new List<UserBadgeItem>();
            }

            var badgeIds = rows
                .Select(row => row.BadgeId)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            var definitions = badgeIds.Count == 0
                ? new Dictionary<long, RawBadgeDefinitionRecord>()
                : (await GetRestRecordsAsync<RawBadgeDefinitionRecord>(
                    "badge_definitions?select=id,name,description,image_url" +
                    $"&id=in.({string.Join(",", badgeIds)})"))
                    .ToDictionary(definition => definition.Id);

            return rows
                .Select(row =>
                {
                    definitions.TryGetValue(row.BadgeId, out var definition);
                    return new UserBadgeItem
                    {
                        Id = row.Id,
                        BadgeId = row.BadgeId,
                        Name = string.IsNullOrWhiteSpace(definition?.Name)
                            ? $"徽章 #{row.BadgeId}"
                            : definition!.Name!,
                        Description = definition?.Description,
                        ImageUrl = definition?.ImageUrl,
                        IsDisplayed = row.IsDisplayed,
                        ExpiresAt = row.ExpiresAt,
                    };
                })
                .ToList();
        }

        private async Task PatchCurrentUserProfileFieldsAsync(
            string userId,
            Dictionary<string, object?> fields)
        {
            fields["updated_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            await PatchRestRecordAsync(
                $"profiles?id=eq.{Uri.EscapeDataString(userId)}",
                fields);
        }

        private static string? NormalizeOptionalProfileText(string value, int maxLength)
        {
            var normalized = value.Trim();
            if (string.IsNullOrWhiteSpace(normalized)) return null;
            if (normalized.Length > maxLength)
            {
                throw new InvalidOperationException($"输入内容不能超过 {maxLength} 个字符。");
            }

            return normalized;
        }

        private static List<AvatarPresetOption> BuildAvatarPresetOptions()
        {
            var items = new List<AvatarPresetOption>
            {
                new() { FileName = "ic_avatar_default.webp", Label = "默认头像" },
            };

            for (var i = 1; i <= 15; i++)
            {
                items.Add(new AvatarPresetOption
                {
                    FileName = $"ic_avatar_{i:00}.webp",
                    Label = $"预设头像 {i:00}",
                });
            }

            return items;
        }

        public async Task<BookshelfDocument> GetBookshelfAsync()
        {
            var userId = GetRequiredCurrentUserId("请先登录后再查看书架。");

            var favoritesTask = GetInteractionRecordsAsync("comics_favorites", userId);
            var likesTask = GetInteractionRecordsAsync("comic_likes", userId);
            await Task.WhenAll(favoritesTask, likesTask);

            var favoriteRows = favoritesTask.Result;
            var likeRows = likesTask.Result;
            var comicIds = favoriteRows
                .Concat(likeRows)
                .Select(row => row.ComicId)
                .Distinct()
                .ToList();
            var comics = await GetComicRecordsByIdsAsync(comicIds);

            return new BookshelfDocument
            {
                Favorites = MapBookshelfItems(favoriteRows, comics),
                Likes = MapBookshelfItems(likeRows, comics),
            };
        }

        public async Task<ComicSearchDocument> SearchComicsAsync(string searchTerm)
        {
            var normalized = searchTerm.Trim();
            var records = await PostRpcAsync<List<RawComicSearchRecord>>(
                "search_comics_pgroonga",
                new
                {
                    search_term = normalized,
                    page_number = 1,
                    items_per_page = 40,
                    sort_by = string.IsNullOrWhiteSpace(normalized) ? "popularity_weekly" : "updated_at",
                    sort_order = "desc",
                },
                authenticated: false) ?? new List<RawComicSearchRecord>();

            return new ComicSearchDocument
            {
                Query = normalized,
                TotalCount = records.FirstOrDefault()?.TotalCount ?? records.Count,
                Items = records.Select(MapSearchItem).ToList(),
            };
        }

        public async Task<List<ComicListItem>> GetRandomComicsAsync(int limit = 18)
        {
            var safeLimit = Math.Clamp(limit, 1, 100);
            var records = await PostRpcAsync<List<RawComicSearchRecord>>(
                "get_random_comics",
                new
                {
                    p_limit = safeLimit,
                    p_category_id = (long?)null,
                    p_exclude_comic_id = (long?)null,
                },
                authenticated: false) ?? new List<RawComicSearchRecord>();

            return records
                .Select(MapSearchItem)
                .Select(item =>
                {
                    item.ShowSubtitle = false;
                    item.Subtitle = null;
                    return item;
                })
                .ToList();
        }

        public async Task<List<RankingComicItem>> GetRankingComicsAsync(RankingKind kind, int limit = 30)
        {
            var orderColumn = kind switch
            {
                RankingKind.Daily => "popularity_daily",
                RankingKind.Weekly => "popularity_weekly",
                RankingKind.Monthly => "popularity_monthly",
                RankingKind.Rating => "rating_average",
                RankingKind.RatingCount => "rating_count",
                _ => "popularity_daily",
            };

            const string select = "id,title,cover_url,is_finished,rating_average,rating_count," +
                "popularity_daily,popularity_weekly,popularity_monthly";
            var safeLimit = Math.Clamp(limit, 1, 100);

            // slug 非空 = 已正式上架(与 count_comics_by_category 的过滤口径一致)
            var path = $"comics?select={select}&slug=not.is.null" +
                $"&order={orderColumn}.desc.nullslast&limit={safeLimit}";

            // 评分榜额外要求评分人数达到阈值,避免「1 个满分」霸榜
            if (kind == RankingKind.Rating)
            {
                path += $"&rating_count=gte.{MinRatingsForRatingBoard}";
            }

            var records = await GetRestRecordsAsync<RawRankingComicRecord>(path);
            var items = new List<RankingComicItem>();
            var rank = 1;
            foreach (var record in records)
            {
                items.Add(MapRankingItem(record, kind, rank++));
            }
            return items;
        }

        public async Task<RecentReadingProgress?> GetRecentReadingProgressAsync()
        {
            var userId = GetRequiredCurrentUserId("请先登录后再同步阅读进度。");
            var rows = await GetAuthenticatedRestRecordsAsync<RawReadingHistoryRecord>(
                "reading_history?select=comic_id,chapter_id,page_index,chapter_title,total_pages,last_read_at" +
                $"&user_id=eq.{Uri.EscapeDataString(userId)}&order=last_read_at.desc&limit=1");

            var history = rows.FirstOrDefault();
            if (history?.ChapterId is not { } chapterId || history.ComicId <= 0)
            {
                return null;
            }

            var comic = await GetComicRecordByIdAsync(history.ComicId);
            return new RecentReadingProgress
            {
                ComicId = history.ComicId,
                ChapterId = chapterId,
                PageIndex = Math.Max(history.PageIndex ?? 1, 1),
                TotalPages = Math.Max(history.TotalPages ?? 0, 0),
                ComicTitle = comic?.Title ?? "漫画",
                ChapterTitle = string.IsNullOrWhiteSpace(history.ChapterTitle)
                    ? "继续阅读"
                    : history.ChapterTitle!,
                CoverUrl = comic?.CoverUrl,
                LastReadAt = history.LastReadAt,
            };
        }

        public async Task<List<ComicListItem>> GetReadingHistoryAsync()
        {
            var userId = GetRequiredCurrentUserId("请先登录后再查看阅读历史。");
            var rows = await GetAuthenticatedRestRecordsAsync<RawReadingHistoryRecord>(
                "reading_history?select=comic_id,chapter_id,page_index,chapter_title,total_pages,last_read_at" +
                $"&user_id=eq.{Uri.EscapeDataString(userId)}&order=last_read_at.desc&limit=100");

            var comicIds = rows
                .Select(row => row.ComicId)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            var comics = await GetComicRecordsByIdsAsync(comicIds);
            var items = new List<ComicListItem>();

            foreach (var row in rows)
            {
                if (row.ChapterId is not { } chapterId ||
                    !comics.TryGetValue(row.ComicId, out var comic))
                {
                    continue;
                }

                var page = Math.Max(row.PageIndex ?? 1, 1);
                var total = Math.Max(row.TotalPages ?? 0, 0);
                var pageText = total > 0 ? $"第 {Math.Clamp(page, 1, total)}/{total} 页" : "继续阅读";
                var chapter = string.IsNullOrWhiteSpace(row.ChapterTitle) ? "章节" : row.ChapterTitle!.Trim();

                items.Add(new ComicListItem
                {
                    Id = comic.Id.ToString(CultureInfo.InvariantCulture),
                    ComicId = comic.Id,
                    ChapterId = chapterId,
                    StartPage = page,
                    Title = comic.Title ?? "",
                    Subtitle = $"{chapter} · {pageText}",
                    CoverUrl = comic.CoverUrl,
                    UpdatedAt = row.LastReadAt,
                });
            }

            return items;
        }

        public async Task SaveReadingProgressAsync(ComicReaderDocument document, int pageIndex)
        {
            if (document.ComicId <= 0 || document.Chapter.Id <= 0) return;
            if (string.IsNullOrWhiteSpace(CurrentSession?.AccessToken) ||
                string.IsNullOrWhiteSpace(CurrentUser?.Id))
            {
                return;
            }

            var userId = CurrentUser!.Id!;
            var safePageIndex = Math.Clamp(pageIndex, 1, Math.Max(document.Pages.Count, 1));
            var body = new
            {
                user_id = userId,
                comic_id = document.ComicId,
                chapter_id = document.Chapter.Id,
                page_index = safePageIndex,
                chapter_title = document.Chapter.Title,
                chapter_index = document.Chapter.Index,
                total_pages = document.Pages.Count,
                total_chapters = document.TotalChapters,
                last_read_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };

            var existing = await GetAuthenticatedRestRecordsAsync<RawReadingHistoryRecord>(
                "reading_history?select=comic_id" +
                $"&user_id=eq.{Uri.EscapeDataString(userId)}&comic_id=eq.{document.ComicId}&limit=1");

            if (existing.Count > 0)
            {
                await PatchRestRecordAsync(
                    $"reading_history?user_id=eq.{Uri.EscapeDataString(userId)}&comic_id=eq.{document.ComicId}",
                    body);
                return;
            }

            await PostRestRecordAsync("reading_history", body);
        }

        private Task<RawComicRecord?> GetComicRecordByIdAsync(long comicId)
        {
            return GetSingleRestRecordAsync<RawComicRecord>(
                "comics",
                ComicSelectColumns,
                $"id=eq.{comicId}");
        }

        private Task<RawComicRecord?> GetComicRecordBySlugAsync(string slug)
        {
            return GetSingleRestRecordAsync<RawComicRecord>(
                "comics",
                ComicSelectColumns,
                $"slug=eq.{Uri.EscapeDataString(slug)}");
        }

        private Task<RawCategoryRecord?> GetCategoryRecordAsync(long? categoryId)
        {
            if (categoryId == null) return Task.FromResult<RawCategoryRecord?>(null);

            return GetSingleRestRecordAsync<RawCategoryRecord>(
                "categories",
                "id,name,slug",
                $"id=eq.{categoryId.Value}");
        }

        private async Task<List<RawComicTagRecord>> GetComicTagRecordsAsync(long comicId)
        {
            return await GetRestRecordsAsync<RawComicTagRecord>(
                $"comic_tags?select=tag:tags(id,name,normalized_name)&comic_id=eq.{comicId}&limit=30");
        }

        private async Task<List<RawChapterRecord>> GetChapterRecordsAsync(long comicId)
        {
            return await GetRestRecordsAsync<RawChapterRecord>(
                $"chapters?select=id,comic_id,title,idx,category,chapter_folder,image_count,image_format,updated_at&comic_id=eq.{comicId}&order=idx.asc&limit=1000");
        }

        private async Task<bool> HasInteractionAsync(string tableName, string userId, long comicId)
        {
            var rows = await GetAuthenticatedRestRecordsAsync<RawComicInteractionRecord>(
                $"{tableName}?select=comic_id&user_id=eq.{Uri.EscapeDataString(userId)}&comic_id=eq.{comicId}&limit=1");

            return rows.Count > 0;
        }

        private async Task SetInteractionAsync(
            string tableName,
            string userId,
            long comicId,
            bool shouldExist)
        {
            var exists = await HasInteractionAsync(tableName, userId, comicId);
            if (exists == shouldExist) return;

            if (shouldExist)
            {
                await PostRestRecordAsync(tableName, new
                {
                    user_id = userId,
                    comic_id = comicId,
                });
                return;
            }

            await DeleteRestRecordsAsync(
                $"{tableName}?user_id=eq.{Uri.EscapeDataString(userId)}&comic_id=eq.{comicId}");
        }

        private Task<List<RawComicInteractionRecord>> GetInteractionRecordsAsync(
            string tableName,
            string userId)
        {
            return GetAuthenticatedRestRecordsAsync<RawComicInteractionRecord>(
                $"{tableName}?select=comic_id,created_at&user_id=eq.{Uri.EscapeDataString(userId)}&order=created_at.desc&limit=200");
        }

        private async Task<Dictionary<long, RawComicRecord>> GetComicRecordsByIdsAsync(List<long> comicIds)
        {
            if (comicIds.Count == 0) return new Dictionary<long, RawComicRecord>();

            var ids = string.Join(",", comicIds.Distinct().OrderBy(id => id));
            var records = await GetRestRecordsAsync<RawComicRecord>(
                $"comics?select={ComicSelectColumns}&id=in.({ids})&limit={comicIds.Count}");

            return records
                .GroupBy(record => record.Id)
                .ToDictionary(group => group.Key, group => group.First());
        }

        private async Task<RawReaderImageResponse> InvokeReaderImageUrlAsync(
            long comicId,
            long chapterId,
            int imageCount,
            string? imageFormat,
            bool useUpscaled)
        {
            if (string.IsNullOrWhiteSpace(_supabaseUrl) || string.IsNullOrWhiteSpace(_supabaseAnonKey))
            {
                throw new InvalidOperationException("SupabaseService 尚未初始化,无法读取章节图片。");
            }

            var functionName = useUpscaled ? "vip-image-url" : "sd-image-url";

            // vip-image-url 必须登录
            if (useUpscaled && string.IsNullOrWhiteSpace(CurrentSession?.AccessToken))
            {
                throw new InvalidOperationException("AI 超分需要登录后才能使用。");
            }

            var pages = BuildReaderPageLabels(imageCount);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var signature = BuildReaderSignature(comicId, chapterId, pages, timestamp);

            var body = new Dictionary<string, object>
            {
                ["comic_id"] = comicId,
                ["chapter_id"] = chapterId,
                ["pages"] = pages,
                ["timestamp"] = timestamp,
                ["signature"] = signature,
            };

            var token = CurrentSession?.AccessToken ?? _supabaseAnonKey;
            string responseBody;
            try
            {
                responseBody = await Client.Functions.Invoke(
                    functionName,
                    token,
                    new Supabase.Functions.Client.InvokeFunctionOptions
                    {
                        Body = body,
                    });
            }
            catch (Supabase.Functions.Exceptions.FunctionsException ex)
            {
                // 429 时把 body 解出来,翻译成更友好的错误
                var bodyText = ex.Content ?? "";
                Debug.WriteLine($"[reader] {functionName} exception: {ex.Message}; body={bodyText}");

                if (TryParseReaderError(bodyText, out var errorCode))
                {
                    throw errorCode switch
                    {
                        "FREE_QUOTA_EXCEEDED" => new InvalidOperationException(
                            "今日 AI 超分的免费额度已经用完,开通 VIP 可以无限制阅读高清资源。"),
                        "ANON_QUOTA_EXCEEDED" => new InvalidOperationException(
                            "匿名阅读次数已用完,登录后即可继续阅读。"),
                        _ => new InvalidOperationException(bodyText),
                    };
                }
                throw;
            }
            Debug.WriteLine($"[reader] {functionName}\n{responseBody}");

            return JsonConvert.DeserializeObject<RawReaderImageResponse>(responseBody)
                ?? new RawReaderImageResponse();
        }

        private static bool TryParseReaderError(string bodyText, out string errorCode)
        {
            errorCode = "";
            if (string.IsNullOrWhiteSpace(bodyText)) return false;

            try
            {
                var parsed = JsonConvert.DeserializeObject<RawReaderImageResponse>(bodyText);
                if (!string.IsNullOrWhiteSpace(parsed?.Error))
                {
                    errorCode = parsed!.Error!;
                    return true;
                }
            }
            catch
            {
                // body 不是 JSON 就吞掉
            }

            return false;
        }

        private string BuildReaderSignature(
            long comicId,
            long chapterId,
            IReadOnlyCollection<string> pages,
            long timestamp)
        {
            if (string.IsNullOrWhiteSpace(_readerClientVerifySecret) ||
                string.IsNullOrWhiteSpace(_readerClientFingerprint))
            {
                return new string('0', 64);
            }

            var normalizedPages = string.Join(",", pages
                .Where(page => !string.IsNullOrWhiteSpace(page))
                .Select(page => page.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(page => page, StringComparer.Ordinal));
            var payload = $"v1|comic_id={comicId}|chapter_id={chapterId}|pages={normalizedPages}|timestamp={timestamp}";

            var baseSecret = HexToBytes(_readerClientVerifySecret, "APP_CLIENT_VERIFY_SECRET");
            var fingerprint = HexToBytes(SelectFirstHexValue(_readerClientFingerprint), "客户端签名指纹");
            var dynamicKeyInput = new byte[baseSecret.Length + fingerprint.Length];
            Buffer.BlockCopy(baseSecret, 0, dynamicKeyInput, 0, baseSecret.Length);
            Buffer.BlockCopy(fingerprint, 0, dynamicKeyInput, baseSecret.Length, fingerprint.Length);
            var dynamicKey = SHA256.HashData(dynamicKeyInput);
            using var hmac = new HMACSHA256(dynamicKey);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static List<string> BuildReaderPageLabels(int imageCount)
            => Enumerable.Range(1, imageCount)
                .Select(page => page.ToString("000", CultureInfo.InvariantCulture))
                .ToList();

        private static string SelectFirstHexValue(string hexValue)
        {
            return hexValue
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? hexValue.Trim();
        }

        private static byte[] HexToBytes(string hexValue, string settingName)
        {
            var normalized = new string(hexValue
                .Where(character => !char.IsWhiteSpace(character))
                .ToArray());

            if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[2..];
            }

            if (normalized.Length == 0 || normalized.Length % 2 != 0)
            {
                throw new InvalidOperationException($"{settingName} 必须是偶数长度的十六进制字符串。");
            }

            try
            {
                return Convert.FromHexString(normalized);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"{settingName} 必须是有效的十六进制字符串。", ex);
            }
        }

        private static int TryParsePageNumber(string? page)
        {
            if (string.IsNullOrWhiteSpace(page)) return 0;

            var normalized = page.Trim();
            var extensionIndex = normalized.IndexOf('.', StringComparison.Ordinal);
            if (extensionIndex > 0)
            {
                normalized = normalized[..extensionIndex];
            }

            return int.TryParse(
                normalized,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var pageNumber)
                ? pageNumber
                : 0;
        }

        private static int GetChapterCategoryOrder(string category) => category switch
        {
            "normal" => 0,
            "single" => 1,
            "volume" => 2,
            "special" => 3,
            _ => 9,
        };

        private async Task<T?> GetSingleRestRecordAsync<T>(
            string tableName,
            string selectColumns,
            string filter)
        {
            var records = await GetRestRecordsAsync<T>(
                $"{tableName}?select={selectColumns}&{filter}&limit=1");

            return records.FirstOrDefault();
        }

        private async Task<List<T>> GetRestRecordsAsync<T>(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(_supabaseUrl) || string.IsNullOrWhiteSpace(_supabaseAnonKey))
            {
                throw new InvalidOperationException("SupabaseService 尚未初始化,无法读取 REST 数据。");
            }

            var requestUri = $"{_supabaseUrl}/rest/v1/{relativePath}";
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.TryAddWithoutValidation("apikey", _supabaseAnonKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _supabaseAnonKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Debug.WriteLine($"[supabase-rest] GET {relativePath}: {(int)response.StatusCode}\n{body}");

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Supabase REST 请求失败: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
            }

            return JsonConvert.DeserializeObject<List<T>>(body) ?? new List<T>();
        }

        private async Task<List<T>> GetAuthenticatedRestRecordsAsync<T>(string relativePath)
        {
            var token = GetRequiredAccessToken();
            return await SendRestAsync<List<T>>(
                HttpMethod.Get,
                relativePath,
                token,
                content: null,
                parseList: true) ?? new List<T>();
        }

        private async Task PostRestRecordAsync(string tableName, object body)
        {
            var token = GetRequiredAccessToken();
            var json = JsonConvert.SerializeObject(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            await SendRestAsync<object>(
                HttpMethod.Post,
                tableName,
                token,
                content,
                parseList: false,
                prefer: "return=minimal");
        }

        private async Task DeleteRestRecordsAsync(string relativePath)
        {
            var token = GetRequiredAccessToken();
            await SendRestAsync<object>(
                HttpMethod.Delete,
                relativePath,
                token,
                content: null,
                parseList: false,
                prefer: "return=minimal");
        }

        private async Task PatchRestRecordAsync(string relativePath, object body)
        {
            var token = GetRequiredAccessToken();
            var json = JsonConvert.SerializeObject(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            await SendRestAsync<object>(
                HttpMethod.Patch,
                relativePath,
                token,
                content,
                parseList: false,
                prefer: "return=minimal");
        }

        private async Task<RawPremiumQuota?> GetPremiumQuotaAsync()
        {
            var token = GetRequiredAccessToken();
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var raw = await SendRestAsync<object>(
                HttpMethod.Post,
                "rpc/get_premium_quota",
                token,
                content,
                parseList: true);

            if (raw == null) return null;

            var json = raw.ToString();
            if (string.IsNullOrWhiteSpace(json)) return null;

            var trimmed = json.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return JsonConvert.DeserializeObject<List<RawPremiumQuota>>(trimmed)?.FirstOrDefault();
            }

            return JsonConvert.DeserializeObject<RawPremiumQuota>(trimmed);
        }

        private async Task<T?> PostRpcAsync<T>(string rpcName, object body, bool authenticated)
        {
            var token = authenticated ? GetRequiredAccessToken() : _supabaseAnonKey;
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("SupabaseService 尚未初始化,无法调用 RPC。");
            }

            var json = JsonConvert.SerializeObject(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            return await SendRestAsync<T>(
                HttpMethod.Post,
                $"rpc/{rpcName}",
                token,
                content,
                parseList: true);
        }

        private async Task<T?> SendRestAsync<T>(
            HttpMethod method,
            string relativePath,
            string bearerToken,
            HttpContent? content,
            bool parseList,
            string? prefer = null)
        {
            if (string.IsNullOrWhiteSpace(_supabaseUrl) || string.IsNullOrWhiteSpace(_supabaseAnonKey))
            {
                throw new InvalidOperationException("SupabaseService 尚未初始化,无法访问 REST 数据。");
            }

            var requestUri = $"{_supabaseUrl}/rest/v1/{relativePath}";
            using var request = new HttpRequestMessage(method, requestUri);
            request.Headers.TryAddWithoutValidation("apikey", _supabaseAnonKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(prefer))
            {
                request.Headers.TryAddWithoutValidation("Prefer", prefer);
            }
            request.Content = content;

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Debug.WriteLine($"[supabase-rest] {method} {relativePath}: {(int)response.StatusCode}\n{body}");

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Supabase REST 请求失败: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
            }

            if (string.IsNullOrWhiteSpace(body) || !parseList) return default;

            return JsonConvert.DeserializeObject<T>(body);
        }

        private async Task UploadStorageObjectAsync(
            string bucket,
            string objectName,
            byte[] data,
            string contentType)
        {
            if (string.IsNullOrWhiteSpace(_supabaseUrl) || string.IsNullOrWhiteSpace(_supabaseAnonKey))
            {
                throw new InvalidOperationException("SupabaseService 尚未初始化,无法上传文件。");
            }

            var token = GetRequiredAccessToken();
            var requestUri = $"{_supabaseUrl}/storage/v1/object/{Uri.EscapeDataString(bucket)}/{Uri.EscapeDataString(objectName)}";
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
            request.Headers.TryAddWithoutValidation("apikey", _supabaseAnonKey);
            request.Headers.TryAddWithoutValidation("x-upsert", "true");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new ByteArrayContent(data);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            request.Content.Headers.ContentLength = data.Length;

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Debug.WriteLine($"[supabase-storage] upload {bucket}/{objectName}: {(int)response.StatusCode}\n{body}");

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Supabase Storage 上传失败: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
            }
        }

        private string GetRequiredAccessToken()
        {
            var token = CurrentSession?.AccessToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("请先登录后再使用需要账户同步的功能。");
            }

            return token;
        }

        private string GetRequiredCurrentUserId(string message)
        {
            if (string.IsNullOrWhiteSpace(CurrentSession?.AccessToken) ||
                string.IsNullOrWhiteSpace(CurrentUser?.Id))
            {
                throw new InvalidOperationException(message);
            }

            return CurrentUser!.Id!;
        }

        private static HomeFeedResponse MapHomeFeed(RawHomeFeedData data)
        {
            var resp = new HomeFeedResponse();

            if (data.Banners != null)
            {
                foreach (var b in data.Banners)
                {
                    resp.Banners.Add(new HomeFeedBanner
                    {
                        Id = b.Id ?? "",
                        Title = b.Title,
                        ImageUrl = b.ImageUrl,
                        TargetType = b.TargetType,
                        TargetValue = b.TargetValue,
                    });
                }
            }

            AddSection(resp.Sections, "recommended", "为你推荐", data.Recommended);
            AddSection(resp.Sections, "newlyAdded", "新作上架", data.NewlyAdded);
            AddSection(resp.Sections, "recent", "最近更新", data.Recent);
            AddSection(resp.Sections, "popular-daily", "日榜", data.Popular?.Daily);
            AddSection(resp.Sections, "popular-weekly", "周榜", data.Popular?.Weekly);
            AddSection(resp.Sections, "popular-monthly", "月榜", data.Popular?.Monthly);

            return resp;
        }

        private static void AddSection(
            List<HomeFeedSection> sections,
            string id,
            string title,
            List<RawMangaItem>? rawItems)
        {
            if (rawItems == null || rawItems.Count == 0) return;

            sections.Add(new HomeFeedSection
            {
                Id = id,
                Title = title,
                Items = rawItems.Select(MapItem).ToList(),
            });
        }

        private static HomeFeedItem MapItem(RawMangaItem m)
        {
            var subtitle = string.Join(" · ", new[]
                {
                    m.CategoryName,
                    m.LatestChapterTitle,
                }.Where(s => !string.IsNullOrEmpty(s)));

            return new HomeFeedItem
            {
                Id = m.DocumentId ?? "",
                Title = m.Name ?? "",
                Author = string.IsNullOrEmpty(subtitle) ? null : subtitle,
                CoverUrl = m.Cover,
                UpdatedAt = m.LatestChapterUpdatedAt,
            };
        }

        private static List<BookshelfComicItem> MapBookshelfItems(
            List<RawComicInteractionRecord> rows,
            IReadOnlyDictionary<long, RawComicRecord> comics)
        {
            var items = new List<BookshelfComicItem>();

            foreach (var row in rows)
            {
                if (!comics.TryGetValue(row.ComicId, out var comic)) continue;

                var subtitleParts = new[]
                {
                    comic.LatestChapterTitle,
                    comic.LatestChapterUpdatedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                }.Where(part => !string.IsNullOrWhiteSpace(part));

                items.Add(new BookshelfComicItem
                {
                    Id = comic.Id.ToString(CultureInfo.InvariantCulture),
                    ComicId = comic.Id,
                    Title = comic.Title ?? "",
                    Subtitle = string.Join(" · ", subtitleParts),
                    CoverUrl = comic.CoverUrl,
                    AddedAt = row.CreatedAt,
                });
            }

            return items;
        }

        private static ComicListItem MapSearchItem(RawComicSearchRecord record)
        {
            var status = record.IsFinished ? "完结" : "连载中";
            var chapters = record.ChaptersCount is > 0 ? $"{record.ChaptersCount} 话" : "章节未知";
            var rating = record.RatingCount is > 0 ? $"评分 {record.RatingAverage ?? 0:0.0}" : "暂无评分";

            return new ComicListItem
            {
                Id = record.Id.ToString(CultureInfo.InvariantCulture),
                ComicId = record.Id,
                Title = record.Title ?? "",
                Subtitle = $"{status} · {chapters} · {rating}",
                CoverUrl = record.CoverUrl,
            };
        }

        private const int MinRatingsForRatingBoard = 10;

        private static RankingComicItem MapRankingItem(RawRankingComicRecord record, RankingKind kind, int rank)
        {
            var subtitle = kind switch
            {
                RankingKind.Daily => $"日人气 {record.PopularityDaily ?? 0}",
                RankingKind.Weekly => $"周人气 {record.PopularityWeekly ?? 0}",
                RankingKind.Monthly => $"月人气 {record.PopularityMonthly ?? 0}",
                RankingKind.Rating => $"评分 {record.RatingAverage ?? 0:0.0} · {record.RatingCount ?? 0} 人",
                RankingKind.RatingCount => $"{record.RatingCount ?? 0} 人评分",
                _ => "",
            };

            return new RankingComicItem
            {
                Rank = rank,
                Id = record.Id.ToString(CultureInfo.InvariantCulture),
                Title = record.Title ?? "",
                CoverUrl = record.CoverUrl,
                Subtitle = subtitle,
            };
        }

        private const string ComicSelectColumns =
            "id,title,slug,summary,note,cover_url,poster_url,authors,region,category_id," +
            "lock_status,is_finished,has_upscaled,release_date,rating_average,rating_count," +
            "view_count,latest_chapter_title,latest_chapter_updated_at";
    }
}
