using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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

        public async Task<ComicReaderDocument> GetComicReaderAsync(long comicId, long chapterId)
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

            var imageResponse = await InvokeReaderImageUrlAsync(
                comicId,
                chapterId,
                selectedChapter.ImageCount,
                selectedChapter.ImageFormat);
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
                Pages = pageImages,
                PlatformRouted = imageResponse.Metadata?.PlatformRouted ?? "",
            };
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

        private async Task<RawReaderImageResponse> InvokeReaderImageUrlAsync(
            long comicId,
            long chapterId,
            int imageCount,
            string? imageFormat)
        {
            if (string.IsNullOrWhiteSpace(_supabaseUrl) || string.IsNullOrWhiteSpace(_supabaseAnonKey))
            {
                throw new InvalidOperationException("SupabaseService 尚未初始化,无法读取章节图片。");
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
            var responseBody = await Client.Functions.Invoke(
                "sd-image-url",
                token,
                new Supabase.Functions.Client.InvokeFunctionOptions
                {
                    Body = body,
                });
            Debug.WriteLine($"[reader] sd-image-url\n{responseBody}");

            return JsonConvert.DeserializeObject<RawReaderImageResponse>(responseBody)
                ?? new RawReaderImageResponse();
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
                        LinkUrl = b.LinkUrl,
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

        private const string ComicSelectColumns =
            "id,title,slug,summary,note,cover_url,poster_url,authors,region,category_id," +
            "lock_status,is_finished,has_upscaled,release_date,rating_average,rating_count," +
            "view_count,latest_chapter_title,latest_chapter_updated_at";
    }
}
