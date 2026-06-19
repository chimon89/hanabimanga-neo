using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using hanabimanga.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Supabase;
using Supabase.Functions;
using Supabase.Gotrue;
using Supabase.Gotrue.Exceptions;
using Supabase.Realtime.PostgresChanges;
using Client = Supabase.Client;
using PostgrestException = Supabase.Postgrest.Exceptions.PostgrestException;
using static Supabase.Postgrest.Constants;

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
        private readonly object _deviceIdLock = new();
        private string? _clientDeviceId;
        private Supabase.Realtime.RealtimeChannel? _notificationChannel;
        private string? _notificationSubscriptionUserId;
        private string? _notificationSubscriptionAccessToken;
        // 业务层邮箱验证状态以 profiles.email_verified_at 为准,auth.users.email_confirmed_at 不可用。
        // 在 GetCurrentUserProfileAsync 读取到当前用户记录时同步更新;登出时由 SignOutAsync 清空。
        private DateTime? _currentEmailVerifiedAt;
        private string? _currentEmailVerifiedUserId;
        private static readonly string AuthTraceFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "hanabi-auth.log");

        public Client Client =>
            _client ?? throw new InvalidOperationException(
                "SupabaseService 尚未初始化,请先调用 InitializeAsync。");

        public Session? CurrentSession => _client?.Auth.CurrentSession;
        public User? CurrentUser => _client?.Auth.CurrentUser;
        public bool IsInitialized => _client is not null;
        public bool IsSignedIn => !string.IsNullOrWhiteSpace(CurrentSession?.AccessToken);
        public string? CurrentUserId => CurrentUser?.Id ?? ReadJwtSubject(CurrentSession?.AccessToken);
        public string? CurrentEmail => CurrentUser?.Email ?? ReadJwtClaim(CurrentSession?.AccessToken, "email");
        public bool IsCurrentUserEmailVerified =>
            _currentEmailVerifiedUserId == CurrentUserId && _currentEmailVerifiedAt.HasValue;

        /// <summary>当前生效的接口地址(已去除尾部斜杠)。</summary>
        public string? CurrentUrl => _supabaseUrl;

        /// <summary>接口线路实时切换完成后触发,供 UI 刷新订阅与展示。</summary>
        public event EventHandler? EndpointChanged;

        /// <summary>登录 / 退出成功后触发,供未登录提示页主动刷新数据。</summary>
        public event EventHandler? AuthStateChanged;

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

                _client = await CreateClientAsync(url, anonKey, options);
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

        /// <summary>
        /// 实时切换接口线路:用新 URL 重建 Supabase 客户端,会话从磁盘自动还原,
        /// 旧客户端被安全销毁。切换完成后触发 <see cref="EndpointChanged"/>。
        /// </summary>
        public async Task SwitchEndpointAsync(string newUrl)
        {
            if (string.IsNullOrWhiteSpace(newUrl))
            {
                throw new ArgumentException("接口地址不能为空。", nameof(newUrl));
            }

            newUrl = newUrl.TrimEnd('/');

            await _initLock.WaitAsync();
            try
            {
                if (_client is null || string.IsNullOrWhiteSpace(_supabaseAnonKey))
                {
                    throw new InvalidOperationException("SupabaseService 尚未初始化,无法切换线路。");
                }

                if (string.Equals(_supabaseUrl, newUrl, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var oldClient = _client;
                var oldChannel = _notificationChannel;

                var client = await CreateClientAsync(newUrl, _supabaseAnonKey, options: null);

                _client = client;
                _supabaseUrl = newUrl;
                _notificationChannel = null;
                _notificationSubscriptionUserId = null;
                _notificationSubscriptionAccessToken = null;

                TearDownClient(oldClient, oldChannel);
            }
            finally
            {
                _initLock.Release();
            }

            EndpointChanged?.Invoke(this, EventArgs.Empty);
        }

        private static async Task<Client> CreateClientAsync(
            string url, string anonKey, SupabaseOptions? options)
        {
            // AutoConnectRealtime = false:Realtime 的 WebSocket 连接是可选能力,
            // 不能让它的失败(如 CDN 线路未代理 WSS)拖垮 Auth / REST 等核心初始化。
            // Realtime 改为在 SubscribeNotificationsAsync 中按需连接(best-effort)。
            options ??= new SupabaseOptions
            {
                AutoConnectRealtime = false,
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

            // 主动刷新:若磁盘上的 access_token 已过期,立即用 refresh_token 换新。
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

            return client;
        }

        // 销毁旧客户端:取消通知订阅、断开 Realtime、停止令牌自动刷新定时器。
        // 关键是 Auth.Shutdown()——否则新旧两个客户端会同时刷新令牌,
        // 一方轮换 refresh_token 后另一方刷新失败,导致用户被意外登出。
        private static void TearDownClient(
            Client client, Supabase.Realtime.RealtimeChannel? channel)
        {
            if (channel is not null)
            {
                try
                {
                    channel.Unsubscribe();
                    client.Realtime.Remove(channel);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[supabase] old channel teardown failed: {ex.Message}");
                }
            }

            try
            {
                client.Realtime.Disconnect(WebSocketCloseStatus.NormalClosure, "endpoint switch");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[supabase] old realtime disconnect failed: {ex.Message}");
            }

            try
            {
                client.Auth.Shutdown();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[supabase] old auth shutdown failed: {ex.Message}");
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
                    OriginalUrl = item.Url!,
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
            try
            {
                await Client.Auth.SignInWithPassword(email, password);
            }
            catch (Exception ex)
            {
                throw ToFriendlyAuthError(ex);
            }

            AuthStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task<UsernameCheckResult> CheckUsernameAvailableAsync(string username)
        {
            var normalized = username.Trim();
            if (normalized.Length < 3 || normalized.Length > 20)
            {
                return new UsernameCheckResult
                {
                    Available = false,
                    Reason = "length",
                };
            }

            var raw = await PostRpcAsync<JToken>(
                "check_username_available_v2",
                new { p_username = normalized },
                authenticated: false);

            var obj = (raw as JArray)?.FirstOrDefault() as JObject ?? raw as JObject;
            var result = obj?.ToObject<UsernameCheckResult>();
            if (result == null)
            {
                throw new InvalidOperationException("用户名检查服务无响应,请稍后再试。");
            }

            return result;
        }

        public async Task SignUpAsync(
            string email,
            string password,
            string username,
            string? displayName,
            string? inviteCode)
        {
            try
            {
                var metadata = new Dictionary<string, object>
                {
                    ["username"] = username.Trim(),
                    ["device_id"] = GetOrCreateClientDeviceId(),
                };

                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    metadata["display_name"] = displayName.Trim();
                }

                if (!string.IsNullOrWhiteSpace(inviteCode))
                {
                    metadata["inviter_code"] = inviteCode.Trim();
                }

                TraceAuthSignUp(
                    "[auth-signup] request: " +
                    $"endpoint={_supabaseUrl ?? "(null)"}, " +
                    $"email_domain={GetEmailDomain(email)}, " +
                    $"metadata_keys={string.Join(",", metadata.Keys.OrderBy(key => key, StringComparer.Ordinal))}");

                var response = await Client.Auth.SignUp(email, password, new SignUpOptions
                {
                    Data = metadata,
                });

                TraceAuthSignUp($"[auth-signup] response: {DescribeAuthResponse(response)}");
            }
            catch (Exception ex)
            {
                TraceAuthSignUp(
                    "[auth-signup] failed: " +
                    $"{ex.GetType().Name}: {ex.Message}; " +
                    $"inner={ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
                throw ToFriendlySignUpError(ex);
            }
        }

        public async Task SendMagicLinkAsync(
            string email,
            string redirectTo = "hanabimanga://auth")
        {
            try
            {
                await Client.Auth.SignInWithOtp(new SignInWithPasswordlessEmailOptions(email)
                {
                    EmailRedirectTo = redirectTo,
                    ShouldCreateUser = true,
                });
            }
            catch (Exception ex)
            {
                throw ToFriendlyAuthError(ex);
            }
        }

        public async Task<bool> RefreshCurrentUserEmailVerificationAsync()
        {
            var userId = CurrentUserId;
            if (string.IsNullOrWhiteSpace(userId))
            {
                _currentEmailVerifiedAt = null;
                _currentEmailVerifiedUserId = null;
                return false;
            }

            try
            {
                // GetCurrentUserProfileAsync 内部会把 EmailVerifiedAt 同步到缓存字段。
                var profile = await GetCurrentUserProfileAsync(userId);
                return profile?.IsEmailVerified ?? false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[auth-email] refresh verification failed: {ex.Message}");
                return IsCurrentUserEmailVerified;
            }
        }

        /// <summary>
        /// 邮箱验证:请求服务端给当前账号的邮箱发送 6 位 OTP 验证码。
        /// 走独立 Edge Function `request-email-verification`,与 Magic Link 登录解耦,
        /// 邮件正文只包含验证码,不带任何登录链接。后端同时会拦截一次性/临时邮箱。
        /// </summary>
        /// <returns>服务端返回的脱敏邮箱与验证码过期时间,用于 UI 展示。</returns>
        public async Task<EmailVerificationDispatchResult> SendCurrentEmailVerificationOtpAsync(
            string? email = null)
        {
            // email 参数仅用于校验当前账号有合法邮箱,实际发送目标以服务端 JWT 中的 email 为准。
            _ = NormalizeCurrentEmail(email);

            var response = await InvokeEmailVerificationFunctionAsync(
                "request-email-verification",
                new Dictionary<string, object>());

            string? maskedEmail = null;
            DateTime? expiresAt = null;

            if (response != null)
            {
                if (response.TryGetValue("masked_email", out var maskedRaw) && maskedRaw is string maskedStr)
                {
                    maskedEmail = maskedStr;
                }
                if (response.TryGetValue("expires_at", out var expiresRaw) &&
                    expiresRaw is string expiresStr &&
                    DateTime.TryParse(
                        expiresStr,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsedExpiresAt))
                {
                    expiresAt = parsedExpiresAt;
                }
            }

            return new EmailVerificationDispatchResult(maskedEmail, expiresAt);
        }

        /// <summary>
        /// 邮箱验证:把用户输入的 6 位 OTP 交给 RPC `confirm_email_verification` 核对。
        /// 校验通过后服务端会把 profiles.email_verified_at 写为当前时间戳,
        /// 并尝试发放"验证邮箱"任务的积分奖励(返回的 reward 字段记录到账详情)。
        /// </summary>
        public async Task<ConfirmEmailVerificationResponse> VerifyCurrentEmailOtpAsync(
            string token,
            string? email = null)
        {
            _ = NormalizeCurrentEmail(email);
            var normalizedToken = token.Trim();
            if (string.IsNullOrWhiteSpace(normalizedToken))
            {
                throw new InvalidOperationException("请输入邮箱验证码。");
            }

            ConfirmEmailVerificationResponse? response;
            try
            {
                _ = GetRequiredAccessToken();
                response = await Client.Rpc<ConfirmEmailVerificationResponse>(
                    "confirm_email_verification",
                    new Dictionary<string, object>
                    {
                        ["p_code"] = normalizedToken,
                    });
            }
            catch (PostgrestException ex)
            {
                Debug.WriteLine($"[email-otp] confirm_email_verification RPC failed: {ex.Message}; body={ex.Content}");
                throw new InvalidOperationException(
                    "验证失败,请稍后重试或重新获取验证码。",
                    ex);
            }
            catch (HttpRequestException ex)
            {
                Debug.WriteLine($"[email-otp] confirm_email_verification HTTP failed: {ex.Message}");
                throw new InvalidOperationException(
                    "验证失败,请稍后重试或重新获取验证码。",
                    ex);
            }

            if (response == null)
            {
                throw new InvalidOperationException("验证服务无响应,请稍后重试。");
            }

            if (!response.IsVerified)
            {
                // verified=false 且 already_verified=false:后端在 message 给出失败原因。
                var failureMessage = string.IsNullOrWhiteSpace(response.Message)
                    ? "验证码不正确或已过期,请重新获取后再试。"
                    : response.Message!;
                throw new InvalidOperationException(failureMessage);
            }

            // 通过(无论首次 verified 还是 already_verified)都把缓存置为已验证,
            // 同时回查一次 profiles 拿到权威 email_verified_at 时间戳。
            _currentEmailVerifiedAt = DateTime.UtcNow;
            _currentEmailVerifiedUserId = CurrentUserId;
            _ = RefreshCurrentUserEmailVerificationAsync();

            AuthStateChanged?.Invoke(this, EventArgs.Empty);
            return response;
        }

        private async Task<Dictionary<string, object>?> InvokeEmailVerificationFunctionAsync(
            string functionName,
            Dictionary<string, object> body)
        {
            // request-email-verification 沿用当前会话 JWT;未登录时退回 anon key,
            // 让后端按 401/403 自行拒绝,避免客户端先做权限判断与后端规则发生分歧。
            var token = CurrentSession?.AccessToken ?? _supabaseAnonKey;
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("SupabaseService 尚未初始化,无法调用邮箱验证接口。");
            }

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
                var bodyText = ex.Content ?? "";
                Debug.WriteLine($"[email-otp] {functionName} exception: {ex.Message}; body={bodyText}");
                throw new InvalidOperationException(
                    TranslateEmailVerificationError(bodyText),
                    ex);
            }

            Debug.WriteLine($"[email-otp] {functionName}\n{responseBody}");
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, object>>(responseBody);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[email-otp] {functionName} parse failed: {ex.Message}");
                return null;
            }
        }

        // request-email-verification 失败响应模型为 EdgeFunctionError: { "error": "...", "message": "..." }。
        // 优先展示后端给的 message,否则按 error 关键字给一份保底中文文案。
        private static string TranslateEmailVerificationError(string bodyText)
        {
            if (!string.IsNullOrWhiteSpace(bodyText))
            {
                try
                {
                    var parsed = JsonConvert.DeserializeObject<Dictionary<string, object>>(bodyText);
                    if (parsed != null)
                    {
                        var message = parsed.TryGetValue("message", out var m) ? m?.ToString() : null;
                        if (!string.IsNullOrWhiteSpace(message)) return message!;

                        var errorKey = parsed.TryGetValue("error", out var e) ? e?.ToString() : null;
                        if (!string.IsNullOrWhiteSpace(errorKey)) return errorKey!;
                    }
                }
                catch
                {
                    // 不是 JSON,落到默认文案。
                }
            }

            return "验证码发送失败,请稍后重试。";
        }

        /// <summary>
        /// 处理 Magic Link 邮件回跳的 hanabimanga:// 链接,完成登录并建立会话。
        /// 兼容两种回跳格式:
        ///   - confirm 链接流:query 带一次性 token_hash,经 VerifyTokenHash 换取会话;
        ///   - 隐式流:fragment 直接带 access_token / refresh_token,经 SetSession 建立会话。
        /// 两者都会触发 SignedIn 状态变更,经注入的 SessionHandler 自动落盘持久化。
        /// </summary>
        public async Task CompleteMagicLinkAsync(Uri callbackUri)
        {
            // 参数可能在 query(?token_hash=...)或 fragment(#access_token=...),两处都收集。
            var parameters = ParseUrlParameters(callbackUri.Query);
            foreach (var pair in ParseUrlParameters(callbackUri.Fragment))
            {
                parameters.TryAdd(pair.Key, pair.Value);
            }

            if (parameters.TryGetValue("error_description", out var errorDescription)
                && !string.IsNullOrWhiteSpace(errorDescription))
            {
                throw new InvalidOperationException(errorDescription);
            }

            if (parameters.ContainsKey("error"))
            {
                throw new InvalidOperationException("登录链接无效或已过期,请重新获取。");
            }

            parameters.TryGetValue("type", out var typeRaw);

            try
            {
                if (parameters.TryGetValue("token_hash", out var tokenHash)
                    && !string.IsNullOrWhiteSpace(tokenHash))
                {
                    await Client.Auth.VerifyTokenHash(tokenHash, ParseEmailOtpType(typeRaw));
                }
                else if (parameters.TryGetValue("access_token", out var accessToken)
                         && !string.IsNullOrWhiteSpace(accessToken)
                         && parameters.TryGetValue("refresh_token", out var refreshToken)
                         && !string.IsNullOrWhiteSpace(refreshToken))
                {
                    await Client.Auth.SetSession(accessToken, refreshToken);
                }
                else
                {
                    throw new InvalidOperationException("登录链接缺少必要的令牌信息,请重新获取。");
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw ToFriendlyAuthError(ex);
            }

            AuthStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private static Constants.EmailOtpType ParseEmailOtpType(string? raw) =>
            raw?.Trim().ToLowerInvariant() switch
            {
                "signup" => Constants.EmailOtpType.Signup,
                "invite" => Constants.EmailOtpType.Invite,
                "magiclink" => Constants.EmailOtpType.MagicLink,
                "recovery" => Constants.EmailOtpType.Recovery,
                "email_change" => Constants.EmailOtpType.EmailChange,
                _ => Constants.EmailOtpType.Email,
            };

        private string NormalizeCurrentEmail(string? email)
        {
            var normalizedEmail = string.IsNullOrWhiteSpace(email)
                ? CurrentEmail
                : email.Trim();
            if (string.IsNullOrWhiteSpace(normalizedEmail))
            {
                throw new InvalidOperationException("当前账号缺少邮箱地址,无法发送验证码。");
            }

            return normalizedEmail!;
        }


        private static Dictionary<string, string> ParseUrlParameters(string? raw)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(raw))
            {
                return result;
            }

            foreach (var pair in raw.TrimStart('#', '?')
                         .Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=');
                if (idx <= 0)
                {
                    continue;
                }

                var key = Uri.UnescapeDataString(pair[..idx]);
                var value = Uri.UnescapeDataString(pair[(idx + 1)..]);
                result[key] = value;
            }

            return result;
        }

        public async Task SignOutAsync()
        {
            try
            {
                await Client.Auth.SignOut(Constants.SignOutScope.Local);
            }
            catch (Exception ex)
            {
                throw ToFriendlyAuthError(ex);
            }

            _currentEmailVerifiedAt = null;
            _currentEmailVerifiedUserId = null;

            AuthStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private static InvalidOperationException ToFriendlyAuthError(Exception ex)
        {
            string message;

            if (ex is GotrueException gotrue)
            {
                message = gotrue.Reason switch
                {
                    FailureHint.Reason.UserBadLogin => "邮箱或密码错误,请重新输入。",
                    FailureHint.Reason.UserBadMultiple => "邮箱或密码错误,请重新输入。",
                    FailureHint.Reason.UserBadPassword => "密码不符合要求(至少 6 位)。",
                    FailureHint.Reason.UserBadEmailAddress => "邮箱格式不正确。",
                    FailureHint.Reason.UserEmailNotConfirmed => "邮箱尚未验证,请先到邮箱完成确认后再登录。",
                    FailureHint.Reason.UserAlreadyRegistered => "该邮箱已注册,请直接登录。",
                    FailureHint.Reason.UserTooManyRequests => "操作过于频繁,请稍后再试。",
                    FailureHint.Reason.UserMissingInformation => "请填写完整的邮箱和密码。",
                    FailureHint.Reason.Offline => "网络连接失败,请检查网络后重试。",
                    _ => "操作失败,请稍后重试。",
                };
            }
            else if (ex is HttpRequestException || ex is TaskCanceledException)
            {
                message = "网络连接失败,请检查网络后重试。";
            }
            else
            {
                message = "操作失败,请稍后重试。";
            }

            return new InvalidOperationException(message, ex);
        }

        private static InvalidOperationException ToFriendlySignUpError(Exception ex)
        {
            var raw = FlattenExceptionMessage(ex).ToLowerInvariant();
            if (raw.Contains("user already registered", StringComparison.Ordinal) ||
                raw.Contains("already registered", StringComparison.Ordinal))
            {
                return new InvalidOperationException("该邮箱已注册,请直接登录。", ex);
            }

            if ((raw.Contains("profiles_username_key", StringComparison.Ordinal) ||
                 raw.Contains("unique_violation", StringComparison.Ordinal)) &&
                raw.Contains("username", StringComparison.Ordinal))
            {
                return new InvalidOperationException("用户名已被占用,请换一个。", ex);
            }

            return ToFriendlyAuthError(ex);
        }

        private static string FlattenExceptionMessage(Exception ex)
        {
            var builder = new StringBuilder();
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(current.Message);
            }

            return builder.ToString();
        }

        private static string GetEmailDomain(string email)
        {
            var at = email.LastIndexOf('@');
            return at >= 0 && at < email.Length - 1
                ? email[(at + 1)..].Trim().ToLowerInvariant()
                : "(invalid)";
        }

        private string GetOrCreateClientDeviceId()
        {
            if (!string.IsNullOrWhiteSpace(_clientDeviceId)) return _clientDeviceId!;

            lock (_deviceIdLock)
            {
                if (!string.IsNullOrWhiteSpace(_clientDeviceId)) return _clientDeviceId!;

                var path = GetClientDeviceIdPath();
                try
                {
                    if (File.Exists(path))
                    {
                        var existing = File.ReadAllText(path, Encoding.UTF8).Trim();
                        if (!string.IsNullOrWhiteSpace(existing))
                        {
                            _clientDeviceId = existing;
                            return _clientDeviceId;
                        }
                    }

                    _clientDeviceId = Guid.NewGuid().ToString("N");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, _clientDeviceId, Encoding.UTF8);
                    return _clientDeviceId;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[auth-signup] device id persistence failed: {ex.Message}");
                    _clientDeviceId = Guid.NewGuid().ToString("N");
                    return _clientDeviceId;
                }
            }
        }

        private static string GetClientDeviceIdPath()
        {
            try
            {
                return Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                    "auth-device-id.txt");
            }
            catch
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "hanabimanga",
                    "auth-device-id.txt");
            }
        }

        private static void TraceAuthSignUp(string message)
        {
            Debug.WriteLine(message);
            try
            {
                File.AppendAllText(
                    AuthTraceFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[auth-signup] trace write failed: {ex.Message}");
            }
        }

        private static string DescribeAuthResponse(object? response)
        {
            if (response == null) return "null";

            var type = response.GetType();
            var user = type.GetProperty("User")?.GetValue(response);
            var accessToken = type.GetProperty("AccessToken")?.GetValue(response) as string;
            var refreshToken = type.GetProperty("RefreshToken")?.GetValue(response) as string;

            var userType = user?.GetType();
            var userId = userType?.GetProperty("Id")?.GetValue(user)?.ToString();
            var userEmail = userType?.GetProperty("Email")?.GetValue(user)?.ToString();
            var emailConfirmedAt = userType?.GetProperty("EmailConfirmedAt")?.GetValue(user)?.ToString();

            return
                $"type={type.FullName}, " +
                $"has_user={user != null}, " +
                $"user_id={RedactMiddle(userId)}, " +
                $"user_email_domain={GetEmailDomain(userEmail ?? "")}, " +
                $"email_confirmed_at={(string.IsNullOrWhiteSpace(emailConfirmedAt) ? "(null)" : "present")}, " +
                $"has_access_token={!string.IsNullOrWhiteSpace(accessToken)}, " +
                $"has_refresh_token={!string.IsNullOrWhiteSpace(refreshToken)}";
        }

        private static string RedactMiddle(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "(null)";
            if (value.Length <= 8) return "***";

            return $"{value[..4]}...{value[^4..]}";
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
                $"banner_url={profile?.BannerUrl ?? "(null)"}, " +
                $"vip_expiration_date={profile?.VipExpirationDate?.ToString("O") ?? "(null)"}, " +
                $"email_verified_at={profile?.EmailVerifiedAt?.ToString("O") ?? "(null)"}");

            // 当前用户的邮箱验证状态以 profiles.email_verified_at 为准,缓存供 IsCurrentUserEmailVerified 同步访问。
            if (profile != null && string.Equals(profile.Id, CurrentUserId, StringComparison.Ordinal))
            {
                _currentEmailVerifiedAt = profile.EmailVerifiedAt;
                _currentEmailVerifiedUserId = profile.Id;
            }

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
                BannerPresets = BuildBannerPresetOptions(),
            };
        }

        public async Task<TaskCenterDocument?> GetCurrentUserTaskCenterAsync()
        {
            var token = CurrentSession?.AccessToken;
            var userId = CurrentUserId;
            var isSignedIn = !string.IsNullOrWhiteSpace(token) &&
                !string.IsNullOrWhiteSpace(userId);

            if (!isSignedIn)
            {
                throw new InvalidOperationException("请先登录后再查看任务中心。");
            }

            var taskDefinitions = await TryGetTaskCenterRecordsAsync(
                () => GetRestRecordsWithOptionalAuthAsync<RawTaskDefinitionRecord>(
                    "task_definitions?select=id,title,description,category,task_type,is_active,sort_order,created_at,updated_at" +
                    "&is_active=eq.true&order=sort_order.asc,created_at.asc&limit=100"));
            var products = await TryGetTaskCenterRecordsAsync(
                () => GetRestRecordsAsync<RawPointProductRecord>(
                    "products?select=id,name,description,price,image_url,duration_days,is_active,sort_order,stock_limit,sales_count,type" +
                    "&is_active=eq.true&order=sort_order.asc&limit=20"));

            var escapedUserId = Uri.EscapeDataString(userId!);
            var todayStartUtc = DateTime.Now.Date.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);
            var escapedTodayStart = Uri.EscapeDataString(todayStartUtc);

            var ledgerRows = await TryGetTaskCenterRecordsAsync(
                () => GetAuthenticatedRestRecordsAsync<RawPointLedgerRecord>(
                    "points_ledger?select=id,user_id,amount,reason,created_at" +
                    $"&user_id=eq.{escapedUserId}&order=created_at.desc&limit=200"));
            var progressRows = await TryGetTaskCenterRecordsAsync(
                () => GetAuthenticatedRestRecordsAsync<RawUserTaskProgressRecord>(
                    "user_task_progress?select=user_id,task_id,period_key" +
                    $"&user_id=eq.{escapedUserId}&limit=500"));
            var profile = await TryGetTaskCenterRecordAsync(
                () => GetCurrentUserProfileAsync(userId!));
            var todayComments = await TryGetTaskCenterRecordsAsync(
                () => GetAuthenticatedRestRecordsAsync<RawCommentRecord>(
                    "comments?select=id,created_at" +
                    $"&user_id=eq.{escapedUserId}&created_at=gte.{escapedTodayStart}&limit=100"));
            var todayReadingRows = await TryGetTaskCenterRecordsAsync(
                () => GetAuthenticatedRestRecordsAsync<RawReadingHistoryRecord>(
                    "reading_history?select=user_id,comic_id,page_index,total_pages,last_read_at" +
                    $"&user_id=eq.{escapedUserId}&last_read_at=gte.{escapedTodayStart}&limit=100"));
            var todayChapterViews = await TryGetTaskCenterRecordsAsync(
                () => GetAuthenticatedRestRecordsAsync<RawChapterViewLogRecord>(
                    "chapter_view_logs?select=user_id,chapter_id,viewed_at" +
                    $"&user_id=eq.{escapedUserId}&viewed_at=gte.{escapedTodayStart}&limit=100"));

            var todayReadProgress = Math.Max(
                todayChapterViews.Count,
                todayReadingRows.Sum(row => Math.Clamp(row.PageIndex ?? 0, 0, 20)));

            RawCheckinWeekPreview? checkinPreview = null;
            try
            {
                checkinPreview = await GetCheckinWeekPreviewAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[task-center] checkin preview failed: {ex.Message}");
            }

            return BuildTaskCenterDocument(
                userId!,
                ledgerRows,
                taskDefinitions,
                progressRows,
                products,
                todayComments.Count,
                Math.Clamp(todayReadProgress, 0, 20),
                checkinPreview,
                profile);
        }

        private async Task<RawCheckinWeekPreview?> GetCheckinWeekPreviewAsync()
        {
            var raw = await PostRpcAsync<JToken>("get_checkin_week_preview", new { }, authenticated: true);
            var obj = (raw as JArray)?.FirstOrDefault() as JObject ?? raw as JObject;
            return obj?.ToObject<RawCheckinWeekPreview>();
        }

        private const string CheckinTaskId = "daily_checkin";

        public async Task ClaimCheckinRewardAsync()
        {
            GetRequiredCurrentUserId("请先登录后再签到。");

            var raw = await PostRpcAsync<JToken>(
                "claim_task_reward",
                new { p_task_id = CheckinTaskId },
                authenticated: true);

            // claim_task_reward 返回结构未在文档中定义:若返回对象明确含 success=false 则抛错,
            // 否则视为成功,实际结果以随后重新拉取的任务中心文档为准。
            var result = (raw as JArray)?.FirstOrDefault() as JObject ?? raw as JObject;
            if (result?["success"]?.Value<bool>() == false)
            {
                throw new InvalidOperationException(
                    result["error"]?.ToString() ?? "签到失败,请稍后再试。");
            }
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

        public async Task<UserProfile> SetCurrentUserBannerAsync(string bannerValue)
        {
            var userId = GetRequiredCurrentUserId("请先登录后再修改个人页横幅。");
            var normalizedBanner = NormalizeOptionalProfileText(bannerValue, 512)
                ?? "ic_banner_default.webp";

            await PatchCurrentUserProfileFieldsAsync(userId, new Dictionary<string, object?>
            {
                ["banner_url"] = normalizedBanner,
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

        public async Task UpdateCurrentUserPasswordAsync(string currentPassword, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(currentPassword))
            {
                throw new InvalidOperationException("请输入当前密码。");
            }

            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
            {
                throw new InvalidOperationException("新密码至少需要 6 个字符。");
            }

            var userId = GetRequiredCurrentUserId("请先登录后再修改密码。");
            var email = CurrentUser?.Email;
            if (string.IsNullOrWhiteSpace(email))
            {
                throw new InvalidOperationException("无法读取当前账号邮箱，请重新登录后再修改密码。");
            }

            try
            {
                await Client.Auth.SignInWithPassword(email, currentPassword);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[auth] password recheck failed: {ex.Message}");
                throw new InvalidOperationException("当前密码验证失败，请检查后重试。");
            }

            if (CurrentUser?.Id != userId)
            {
                throw new InvalidOperationException("当前会话发生变化，请重新登录后再修改密码。");
            }

            await Client.Auth.Update(new UserAttributes { Password = newPassword });
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

        public async Task<List<ComicComment>> GetComicCommentsAsync(
            long comicId,
            int limit = 100,
            bool publicOnly = false)
        {
            if (comicId <= 0) return new List<ComicComment>();

            var safeLimit = Math.Clamp(limit, 1, 200);
            var statusFilter = publicOnly ? "&status=eq.public" : "";
            var rows = await GetRestRecordsWithOptionalAuthAsync<RawCommentRecord>(
                "comments?select=id,user_id,comic_id,chapter_id,parent_id,content,status,is_spoiler,like_count,reply_count,created_at,updated_at" +
                $"&comic_id=eq.{comicId}&parent_id=is.null{statusFilter}&order=created_at.desc&limit={safeLimit}");

            var profiles = await GetProfilesByIdsAsync(rows
                .Select(row => row.UserId)
                .Where(userId => !string.IsNullOrWhiteSpace(userId))
                .Select(userId => userId!)
                .Distinct(StringComparer.Ordinal)
                .ToList());

            return rows
                .Select(row =>
                {
                    profiles.TryGetValue(row.UserId ?? "", out var profile);
                    return MapComment(row, profile);
                })
                .Where(comment => !string.IsNullOrWhiteSpace(comment.Content))
                .ToList();
        }

        public async Task<ComicComment?> GetRandomComicCommentAsync(long comicId)
        {
            var comments = await GetComicCommentsAsync(comicId, limit: 40, publicOnly: true);
            return comments.Count == 0 ? null : comments[Random.Shared.Next(comments.Count)];
        }

        public async Task SubmitComicCommentAsync(long comicId, string content, bool isSpoiler)
        {
            if (comicId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(comicId), "漫画编号无效。");
            }

            var normalized = content.Trim();
            if (normalized.Length < 2)
            {
                throw new InvalidOperationException("评论至少需要 2 个字符。");
            }

            if (normalized.Length > 1000)
            {
                throw new InvalidOperationException("评论不能超过 1000 个字符。");
            }

            var userId = GetRequiredCurrentUserId("请先登录后再发表评论。");
            await PostRestRecordAsync("comments", new
            {
                user_id = userId,
                comic_id = comicId,
                content = normalized,
                is_spoiler = isSpoiler,
            });
        }

        // ===== 工单 / 资源与反馈 =====

        public async Task<List<FeedbackTicket>> GetFeedbackTicketsAsync(
            string? domain,
            string? status,
            string sortKey)
        {
            var order = sortKey switch
            {
                "updated" => "updated_at.desc.nullslast",
                "newest" => "created_at.desc",
                "oldest" => "created_at.asc",
                _ => "vote_count.desc",
            };

            var query =
                "kanban_tickets?select=id,title,description,domain,category,status,priority," +
                "vote_count,meta_info,admin_response,reporter_id,created_at,updated_at";
            if (!string.IsNullOrWhiteSpace(domain))
            {
                query += $"&domain=eq.{Uri.EscapeDataString(domain)}";
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                query += $"&status=eq.{Uri.EscapeDataString(status)}";
            }
            query += $"&order={order}&limit=60";

            var rows = await GetRestRecordsWithOptionalAuthAsync<RawTicketRecord>(query);

            var profiles = await GetProfilesByIdsAsync(rows
                .Select(row => row.ReporterId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToList());

            var currentUserId = CurrentUser?.Id;
            return rows
                .Select(row =>
                {
                    profiles.TryGetValue(row.ReporterId ?? "", out var profile);
                    return MapTicket(row, profile, currentUserId);
                })
                .Where(ticket => !string.IsNullOrWhiteSpace(ticket.Id))
                .ToList();
        }

        public async Task<HashSet<string>> GetMyTicketVoteIdsAsync()
        {
            var userId = CurrentUser?.Id;
            if (string.IsNullOrWhiteSpace(userId) ||
                string.IsNullOrWhiteSpace(CurrentSession?.AccessToken))
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            var rows = await GetAuthenticatedRestRecordsAsync<RawTicketVoteRecord>(
                $"ticket_votes?select=ticket_id&user_id=eq.{Uri.EscapeDataString(userId)}");

            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.TicketId))
                .Select(row => row.TicketId!)
                .ToHashSet(StringComparer.Ordinal);
        }

        public async Task<List<TicketVoter>> GetTicketVotersAsync(string ticketId)
        {
            if (string.IsNullOrWhiteSpace(ticketId))
            {
                return new List<TicketVoter>();
            }

            var rows = await GetRestRecordsWithOptionalAuthAsync<RawTicketVoteRecord>(
                $"ticket_votes?select=user_id,created_at&ticket_id=eq.{Uri.EscapeDataString(ticketId)}" +
                "&order=created_at.desc&limit=200");

            var profiles = await GetProfilesByIdsAsync(rows
                .Select(row => row.UserId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToList());

            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.UserId))
                .Select(row =>
                {
                    profiles.TryGetValue(row.UserId!, out var profile);
                    var name = profile?.DisplayName;
                    if (string.IsNullOrWhiteSpace(name)) name = profile?.Username;
                    if (string.IsNullOrWhiteSpace(name)) name = "花火用户";
                    return new TicketVoter
                    {
                        UserId = row.UserId!,
                        Name = name!,
                        AvatarUrl = profile?.AvatarUrl,
                        VotedAt = row.CreatedAt,
                    };
                })
                .ToList();
        }

        public async Task AddTicketVoteAsync(string ticketId)
        {
            var userId = GetRequiredCurrentUserId("请先登录后再共鸣。");
            await PostRestRecordAsync("ticket_votes", new
            {
                ticket_id = ticketId,
                user_id = userId,
            });
        }

        public async Task RemoveTicketVoteAsync(string ticketId)
        {
            var userId = GetRequiredCurrentUserId("请先登录后再操作。");
            await DeleteRestRecordsAsync(
                $"ticket_votes?ticket_id=eq.{Uri.EscapeDataString(ticketId)}" +
                $"&user_id=eq.{Uri.EscapeDataString(userId)}");
        }

        public async Task<string> SubmitTicketAsync(
            string title,
            string? description,
            string domain,
            string category,
            object metaInfo)
        {
            GetRequiredCurrentUserId("请先登录后再提交反馈。");

            var trimmedTitle = title.Trim();
            if (trimmedTitle.Length == 0)
            {
                throw new InvalidOperationException("工单标题不能为空。");
            }

            var raw = await PostRpcAsync<JToken>("submit_ticket", new
            {
                p_title = trimmedTitle,
                p_description = string.IsNullOrWhiteSpace(description) ? null : description!.Trim(),
                p_domain = domain,
                p_category = category,
                p_meta_info = metaInfo,
            }, authenticated: true);

            var result = (raw as JArray)?.FirstOrDefault() as JObject ?? raw as JObject;
            if (result == null)
            {
                throw new InvalidOperationException("提交失败:服务未返回结果。");
            }

            if (result["success"]?.Value<bool>() == true)
            {
                return result["ticket_id"]?.ToString() ?? "";
            }

            throw new InvalidOperationException(
                BuildTicketErrorMessage(result["error"]?.ToString(), result));
        }

        public async Task<TicketQuota?> GetTicketQuotaAsync()
        {
            GetRequiredCurrentUserId("请先登录后再查看反馈配额。");

            var raw = await PostRpcAsync<JToken>("get_ticket_quota", new { }, authenticated: true);
            var obj = (raw as JArray)?.FirstOrDefault() as JObject ?? raw as JObject;
            if (obj == null) return null;

            return new TicketQuota
            {
                IsVip = obj["is_vip"]?.Value<bool>() ?? false,
                OpenCount = obj["open_count"]?.Value<int>() ?? 0,
                MaxOpen = obj["max_open"]?.Value<int>() ?? 0,
                Remaining = obj["remaining"]?.Value<int>() ?? 0,
                IsBanned = obj["is_banned"]?.Value<bool>() ?? false,
                IsCoolingDown = obj["is_cooling_down"]?.Value<bool>() ?? false,
                RetryAfter = ParseTicketDate(obj["retry_after"]),
                BannedUntil = ParseTicketDate(obj["banned_until"]),
            };
        }

        private static DateTime? ParseTicketDate(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Date) return token.Value<DateTime>();
            return DateTime.TryParse(token.ToString(), out var parsed) ? parsed : null;
        }

        private static string BuildTicketErrorMessage(string? error, JObject result)
        {
            switch (error)
            {
                case "AUTH_REQUIRED":
                    return "请先登录后再提交反馈。";
                case "BANNED":
                {
                    var until = ParseTicketDate(result["banned_until"]);
                    return until is { } u
                        ? $"反馈提交权限已被限制,将于 {u.ToLocalTime():yyyy-MM-dd HH:mm} 解除。"
                        : "反馈提交权限已被限制。";
                }
                case "COOLDOWN":
                {
                    var retry = ParseTicketDate(result["retry_after"]);
                    return retry is { } r
                        ? $"提交过于频繁,请在 {r.ToLocalTime():HH:mm} 后再试。"
                        : "提交过于频繁,请稍后再试。";
                }
                case "QUOTA_EXCEEDED":
                {
                    var open = result["open_count"]?.ToString();
                    var max = result["max_open"]?.ToString();
                    return string.IsNullOrWhiteSpace(open)
                        ? "未处理的反馈数量已达上限,请等待受理后再提交。"
                        : $"已有 {open}/{max} 条未处理反馈,请等待受理后再提交。";
                }
                default:
                    return "提交失败,请稍后再试。";
            }
        }

        private static FeedbackTicket MapTicket(
            RawTicketRecord record,
            RawProfileSummaryRecord? profile,
            string? currentUserId)
        {
            var meta = record.MetaInfo;
            var bangumiName = meta?["bangumi_name_cn"]?.ToString();
            if (string.IsNullOrWhiteSpace(bangumiName))
            {
                bangumiName = meta?["bangumi_name"]?.ToString();
            }
            var comicTitle = meta?["comic_title"]?.ToString();

            var reporterId = record.ReporterId ?? "";
            var name = profile?.DisplayName;
            if (string.IsNullOrWhiteSpace(name)) name = profile?.Username;
            if (string.IsNullOrWhiteSpace(name)) name = "花火用户";

            return new FeedbackTicket
            {
                Id = record.Id ?? "",
                Title = record.Title ?? "",
                Description = record.Description,
                Domain = string.IsNullOrWhiteSpace(record.Domain) ? "OPS" : record.Domain!,
                Category = string.IsNullOrWhiteSpace(record.Category) ? "其他" : record.Category!,
                Status = string.IsNullOrWhiteSpace(record.Status) ? "RECORDED" : record.Status!,
                Priority = record.Priority ?? 0,
                VoteCount = record.VoteCount ?? 0,
                AdminResponse = record.AdminResponse,
                ReporterId = reporterId,
                CreatedAt = record.CreatedAt,
                UpdatedAt = record.UpdatedAt,
                BangumiName = string.IsNullOrWhiteSpace(bangumiName) ? null : bangumiName,
                ComicTitle = string.IsNullOrWhiteSpace(comicTitle) ? null : comicTitle,
                ReporterName = name!,
                ReporterAvatarUrl = profile?.AvatarUrl,
                IsOwn = !string.IsNullOrWhiteSpace(currentUserId)
                    && string.Equals(currentUserId, reporterId, StringComparison.Ordinal),
            };
        }

        public async Task<UserProfileDocument?> GetUserProfilePageAsync(string userId)
        {
            var normalizedUserId = userId.Trim();
            if (string.IsNullOrWhiteSpace(normalizedUserId)) return null;

            var escapedUserId = Uri.EscapeDataString(normalizedUserId);
            var profileTask = GetSingleRestRecordAsync<RawUserProfileRecord>(
                "profiles",
                "id,username,display_name,avatar_url,banner_url,email_verified_at,created_at",
                $"id=eq.{escapedUserId}");
            var commentsTask = GetRestRecordsWithOptionalAuthAsync<RawCommentRecord>(
                "comments?select=id,user_id,comic_id,chapter_id,parent_id,content,status,is_spoiler,like_count,reply_count,created_at,updated_at" +
                $"&user_id=eq.{escapedUserId}&parent_id=is.null&order=created_at.desc&limit=30");
            var favoritesTask = GetUserInteractionRecordsAsync("comics_favorites", normalizedUserId, 30);
            var likesTask = GetUserInteractionRecordsAsync("comic_likes", normalizedUserId, 30);
            var ratingsTask = GetUserRatingTimelineRecordsAsync(normalizedUserId, 30);

            await Task.WhenAll(profileTask, commentsTask, favoritesTask, likesTask, ratingsTask);

            var profile = profileTask.Result;
            if (profile?.Id == null) return null;

            var commentRows = commentsTask.Result;
            var favoriteRows = favoritesTask.Result;
            var likeRows = likesTask.Result;
            var ratingRows = ratingsTask.Result;

            var comicIds = commentRows
                .Select(row => row.ComicId)
                .Concat(favoriteRows.Select(row => row.ComicId))
                .Concat(likeRows.Select(row => row.ComicId))
                .Concat(ratingRows.Select(row => row.ComicId))
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            var comics = await GetComicRecordsByIdsAsync(comicIds);

            var timeline = new List<UserTimelineItem>();
            foreach (var comment in commentRows)
            {
                if (!comics.TryGetValue(comment.ComicId, out var comic)) continue;
                timeline.Add(new UserTimelineItem
                {
                    Kind = "comment",
                    IconGlyph = "\uE8F2",
                    Title = $"评论了《{comic.Title ?? "漫画"}》",
                    Body = comment.Content,
                    Meta = comment.Status == "public" ? "评论" : GetCommentStatusLabel(comment.Status),
                    CreatedAt = comment.CreatedAt,
                    ComicId = comic.Id,
                    ComicTitle = comic.Title,
                    ComicCoverUrl = comic.CoverUrl,
                });
            }

            foreach (var favorite in favoriteRows)
            {
                if (!comics.TryGetValue(favorite.ComicId, out var comic)) continue;
                timeline.Add(new UserTimelineItem
                {
                    Kind = "favorite",
                    IconGlyph = "\uE734",
                    Title = $"收藏了《{comic.Title ?? "漫画"}》",
                    Meta = "收藏",
                    CreatedAt = favorite.CreatedAt,
                    ComicId = comic.Id,
                    ComicTitle = comic.Title,
                    ComicCoverUrl = comic.CoverUrl,
                });
            }

            foreach (var like in likeRows)
            {
                if (!comics.TryGetValue(like.ComicId, out var comic)) continue;
                timeline.Add(new UserTimelineItem
                {
                    Kind = "like",
                    IconGlyph = "\uE8E1",
                    Title = $"点赞了《{comic.Title ?? "漫画"}》",
                    Meta = "点赞",
                    CreatedAt = like.CreatedAt,
                    ComicId = comic.Id,
                    ComicTitle = comic.Title,
                    ComicCoverUrl = comic.CoverUrl,
                });
            }

            foreach (var rating in ratingRows)
            {
                if (!comics.TryGetValue(rating.ComicId, out var comic)) continue;
                timeline.Add(new UserTimelineItem
                {
                    Kind = "rating",
                    IconGlyph = "\uE735",
                    Title = $"给《{comic.Title ?? "漫画"}》打了 {rating.Score} 分",
                    Meta = "评分",
                    CreatedAt = rating.UpdatedAt ?? rating.CreatedAt,
                    ComicId = comic.Id,
                    ComicTitle = comic.Title,
                    ComicCoverUrl = comic.CoverUrl,
                });
            }

            var isSelf = string.Equals(CurrentUserId, profile.Id, StringComparison.Ordinal);

            // 看自己页面时顺手把当前用户的 email 验证状态写入缓存,
            // 让顶部账户面板这次刷新后也能立即拿到准确的徽标状态。
            if (isSelf)
            {
                _currentEmailVerifiedAt = profile.EmailVerifiedAt;
                _currentEmailVerifiedUserId = profile.Id;
            }

            return new UserProfileDocument
            {
                Profile = new UserProfileHeader
                {
                    UserId = profile.Id,
                    Username = profile.Username,
                    DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName)
                        ? profile.Username ?? "花火用户"
                        : profile.DisplayName!,
                    AvatarUrl = profile.AvatarUrl,
                    BannerUrl = profile.BannerUrl,
                    CreatedAt = profile.CreatedAt,
                    IsSelf = isSelf,
                    IsEmailVerified = profile.EmailVerifiedAt.HasValue,
                    CommentCount = commentRows.Count,
                    FavoriteCount = favoriteRows.Count,
                    LikeCount = likeRows.Count,
                },
                Timeline = timeline
                    .OrderByDescending(item => item.CreatedAt ?? DateTime.MinValue)
                    .Take(80)
                    .ToList(),
            };
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

        private static List<BannerPresetOption> BuildBannerPresetOptions()
        {
            var items = new List<BannerPresetOption>
            {
                new() { FileName = "ic_banner_default.webp", Label = "默认横幅" },
            };

            for (var i = 1; i <= 15; i++)
            {
                items.Add(new BannerPresetOption
                {
                    FileName = $"ic_banner_{i:00}.webp",
                    Label = $"预设横幅 {i:00}",
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

            // slug 对前端没有影响不需要过滤 = 已正式上架(与 count_comics_by_category 的过滤口径一致)
            var path = $"comics?select={select}" +
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

        public async Task<List<CategoryOption>> GetCategoriesAsync()
        {
            var records = await GetRestRecordsAsync<RawCategoryOption>(
                "categories?select=id,name&order=id");

            var options = new List<CategoryOption>
            {
                new CategoryOption { Id = null, Name = "全部" },
            };
            foreach (var record in records)
            {
                if (string.IsNullOrWhiteSpace(record.Name)) continue;
                options.Add(new CategoryOption { Id = record.Id, Name = record.Name });
            }
            return options;
        }

        public async Task<List<ComicListItem>> GetComicsByFilterAsync(
            long? categoryId,
            string? regionFilter,
            bool? isFinished,
            string orderColumn,
            int offset,
            int limit)
        {
            const string select = "id,title,cover_url,is_finished,rating_average,rating_count";
            var safeLimit = Math.Clamp(limit, 1, 100);
            var safeOffset = Math.Max(offset, 0);

            // slug 非空 = 已正式上架(与排行 / count_comics_by_category 口径一致)
            // id 作为次级排序键,保证分页结果稳定
            var path = $"comics?select={select}&slug=not.is.null" +
                $"&order={orderColumn}.desc.nullslast,id.desc" +
                $"&offset={safeOffset}&limit={safeLimit}";

            if (categoryId is { } id)
            {
                path += $"&category_id=eq.{id}";
            }
            if (!string.IsNullOrWhiteSpace(regionFilter))
            {
                path += $"&region={regionFilter}";
            }
            if (isFinished is { } finished)
            {
                path += $"&is_finished=eq.{(finished ? "true" : "false")}";
            }

            var records = await GetRestRecordsAsync<RawRankingComicRecord>(path);
            return records.Select(MapFilteredComic).ToList();
        }

        public async Task<List<NotificationItem>> GetNotificationsAsync(int limit = 30)
        {
            var userId = GetRequiredCurrentUserId("请先登录后再查看通知。");
            var response = await Client.From<NotificationRecord>()
                .Filter("user_id", Operator.Equals, userId)
                .Order("created_at", Ordering.Descending)
                .Limit(Math.Clamp(limit, 1, 100))
                .Get();

            return response.Models.Select(MapNotification).ToList();
        }

        public async Task MarkNotificationReadAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            GetRequiredCurrentUserId("请先登录后再操作通知。");
            await Client.From<NotificationRecord>()
                .Filter("id", Operator.Equals, id)
                .Set(x => x.IsRead, true)
                .Update();
        }

        public async Task MarkAllNotificationsReadAsync()
        {
            var userId = GetRequiredCurrentUserId("请先登录后再操作通知。");
            await Client.From<NotificationRecord>()
                .Filter("user_id", Operator.Equals, userId)
                .Set(x => x.IsRead, true)
                .Update();
        }

        // 订阅 notifications 表的 INSERT;RLS 保证只收到当前用户的行,另在回调内再校验一次 user_id。
        public async Task SubscribeNotificationsAsync(Action<NotificationItem> onInserted)
        {
            var userId = GetRequiredCurrentUserId("请先登录后再订阅通知。");
            var accessToken = GetRequiredAccessToken();

            if (_notificationChannel is not null &&
                string.Equals(_notificationSubscriptionUserId, userId, StringComparison.Ordinal) &&
                string.Equals(_notificationSubscriptionAccessToken, accessToken, StringComparison.Ordinal))
            {
                return;
            }

            UnsubscribeNotifications();

            // AutoConnectRealtime 已关闭,这里按需建立 WebSocket 连接。
            await ConnectRealtimeAsync();
            Client.Realtime.SetAuth(accessToken);

            var channel = Client.Realtime.Channel($"notifications-{userId}");
            channel.Register(new PostgresChangesOptions(
                "public",
                "notifications",
                PostgresChangesOptions.ListenType.Inserts,
                $"user_id=eq.{Uri.EscapeDataString(userId)}"));
            channel.AddPostgresChangeHandler(
                PostgresChangesOptions.ListenType.Inserts,
                (_, change) =>
                {
                    try
                    {
                        var record = change.Model<NotificationRecord>();
                        if (record is not null && record.UserId == userId)
                        {
                            onInserted(MapNotification(record));
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[notifications] realtime handler failed: {ex.Message}");
                    }
                });

            try
            {
                await channel.Subscribe();
                _notificationChannel = channel;
                _notificationSubscriptionUserId = userId;
                _notificationSubscriptionAccessToken = accessToken;
            }
            catch
            {
                try
                {
                    channel.Unsubscribe();
                    Client.Realtime.Remove(channel);
                }
                catch
                {
                    // best-effort cleanup after a failed subscribe
                }

                throw;
            }
        }

        // 按需连接 Realtime WebSocket(已连接则 SDK 内部忽略)。
        // 带超时:线路异常时避免 ConnectAsync 因 SDK 自动重连而无限挂起。
        private async Task ConnectRealtimeAsync()
        {
            var connectTask = Client.Realtime.ConnectAsync();
            var finished = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(10)));
            if (finished != connectTask)
            {
                throw new TimeoutException("Realtime 连接超时。");
            }

            await connectTask;
        }

        public void UnsubscribeNotifications()
        {
            if (_notificationChannel is null)
            {
                _notificationSubscriptionUserId = null;
                _notificationSubscriptionAccessToken = null;
                return;
            }

            try
            {
                _notificationChannel.Unsubscribe();
                _client?.Realtime.Remove(_notificationChannel);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[notifications] unsubscribe failed: {ex.Message}");
            }
            _notificationChannel = null;
            _notificationSubscriptionUserId = null;
            _notificationSubscriptionAccessToken = null;
        }

        private static NotificationItem MapNotification(NotificationRecord record) => new()
        {
            Id = record.Id,
            Title = record.Title ?? "",
            Body = record.Body,
            Type = record.Type ?? "",
            IsRead = record.IsRead,
            CreatedAt = record.CreatedAt,
        };

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

        private Task<List<RawComicInteractionRecord>> GetUserInteractionRecordsAsync(
            string tableName,
            string userId,
            int limit)
        {
            var safeLimit = Math.Clamp(limit, 1, 100);
            return GetRestRecordsAsync<RawComicInteractionRecord>(
                $"{tableName}?select=comic_id,created_at&user_id=eq.{Uri.EscapeDataString(userId)}&order=created_at.desc&limit={safeLimit}");
        }

        private Task<List<RawUserRatingTimelineRecord>> GetUserRatingTimelineRecordsAsync(
            string userId,
            int limit)
        {
            var safeLimit = Math.Clamp(limit, 1, 100);
            return GetRestRecordsAsync<RawUserRatingTimelineRecord>(
                $"comic_ratings?select=comic_id,score,created_at,updated_at&user_id=eq.{Uri.EscapeDataString(userId)}&order=updated_at.desc.nullslast,created_at.desc&limit={safeLimit}");
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

        private async Task<Dictionary<string, RawProfileSummaryRecord>> GetProfilesByIdsAsync(List<string> userIds)
        {
            if (userIds.Count == 0)
            {
                return new Dictionary<string, RawProfileSummaryRecord>();
            }

            var ids = string.Join(
                ",",
                userIds
                    .Where(userId => !string.IsNullOrWhiteSpace(userId))
                    .Distinct(StringComparer.Ordinal)
                    .Select(Uri.EscapeDataString));

            if (string.IsNullOrWhiteSpace(ids))
            {
                return new Dictionary<string, RawProfileSummaryRecord>();
            }

            var records = await GetRestRecordsAsync<RawProfileSummaryRecord>(
                $"profiles?select=id,username,display_name,avatar_url&id=in.({ids})&limit={userIds.Count}");

            return records
                .Where(profile => !string.IsNullOrWhiteSpace(profile.Id))
                .GroupBy(profile => profile.Id!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
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

        private async Task<T?> InvokePaymentFunctionAsync<T>(
            string functionName,
            Dictionary<string, object> body)
        {
            if (string.IsNullOrWhiteSpace(_supabaseUrl) || string.IsNullOrWhiteSpace(_supabaseAnonKey))
            {
                throw new InvalidOperationException("SupabaseService 尚未初始化,无法访问支付系统。");
            }

            var token = GetRequiredAccessToken();
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
                var bodyText = ex.Content ?? "";
                Debug.WriteLine($"[payment] {functionName} exception: {ex.Message}; body={bodyText}");
                throw new InvalidOperationException(
                    TryReadPaymentError(bodyText) ?? "支付接口暂时不可用,请稍后重试。",
                    ex);
            }

            Debug.WriteLine($"[payment] {functionName}\n{responseBody}");
            return string.IsNullOrWhiteSpace(responseBody)
                ? default
                : JsonConvert.DeserializeObject<T>(responseBody);
        }

        private static PaymentOrder MapPaymentOrder(RawPaymentOrderData data)
            => new()
            {
                Id = data.Id ?? data.OrderId ?? "",
                TradeNo = data.TradeNo ?? "",
                HypayTradeNo = data.HypayTradeNo,
                Amount = data.Amount,
                Status = string.IsNullOrWhiteSpace(data.Status) ? "pending" : data.Status!,
                PayUrl = string.IsNullOrWhiteSpace(data.PayUrl) ? data.PayInfo : data.PayUrl,
                PaidAt = data.PaidAt,
                ProductSnapshot = data.ProductSnapshot,
                Synced = data.Synced == true,
            };

        private static string? TryReadPaymentError(string bodyText)
        {
            if (string.IsNullOrWhiteSpace(bodyText)) return null;

            try
            {
                var obj = JObject.Parse(bodyText);
                return obj["error"]?.ToString()
                    ?? obj["message"]?.ToString()
                    ?? obj["msg"]?.ToString();
            }
            catch
            {
                return bodyText.Length <= 160 ? bodyText : null;
            }
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

        private static async Task<List<T>> TryGetTaskCenterRecordsAsync<T>(Func<Task<List<T>>> loader)
        {
            try
            {
                return await loader();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[task-center] REST probe failed: {ex.Message}");
                return new List<T>();
            }
        }

        private static async Task<T?> TryGetTaskCenterRecordAsync<T>(Func<Task<T?>> loader)
        {
            try
            {
                return await loader();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[task-center] record probe failed: {ex.Message}");
                return default;
            }
        }

        private static TaskCenterDocument BuildTaskCenterDocument(
            string userId,
            List<RawPointLedgerRecord> ledgerRows,
            List<RawTaskDefinitionRecord> taskDefinitions,
            List<RawUserTaskProgressRecord> progressRows,
            List<RawPointProductRecord> products,
            int todayCommentCount,
            int todayReadProgress,
            RawCheckinWeekPreview? checkinPreview,
            UserProfile? profile)
        {
            var document = new TaskCenterDocument();
            var now = DateTime.Now;
            var today = now.Date;
            document.IsPermanentVip = profile?.IsPermanentVip == true;
            document.InviteCode = profile?.InviteCode?.Trim() ?? "";
            var ledger = ledgerRows
                .Where(row => string.IsNullOrWhiteSpace(userId) ||
                    string.Equals(row.UserId, userId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(row => row.CreatedAt ?? DateTime.MinValue)
                .ToList();

            document.EarnedPoints = ledger.Where(row => row.Amount > 0).Sum(row => row.Amount);
            document.SpentPoints = Math.Abs(ledger.Where(row => row.Amount < 0).Sum(row => row.Amount));
            document.Points = Math.Max(0, document.EarnedPoints - document.SpentPoints);
            document.TodayPoints = ledger
                .Where(row => row.Amount > 0 && ToLocalDate(row.CreatedAt) == today)
                .Sum(row => row.Amount);
            if (checkinPreview?.Days is { Count: > 0 })
            {
                ApplyCheckinPreview(document, checkinPreview);
            }
            else
            {
                document.HasSignedInToday = ledger.Any(row =>
                    row.Amount > 0 &&
                    ToLocalDate(row.CreatedAt) == today &&
                    IsSignInReason(row.Reason));
                document.SignInStreak = CalculateSignInStreak(ledger, today);
                document.SignInDays = BuildSignInDays(ledger, today);
            }
            document.Transactions = ledger
                .Select(row => new PointTransaction
                {
                    Id = row.Id.ToString(CultureInfo.InvariantCulture),
                    Title = FormatLedgerReason(row.Reason),
                    TimeText = FormatRelativeTime(row.CreatedAt),
                    Amount = row.Amount,
                    Type = row.Amount >= 0 ? "income" : "expense",
                })
                .ToList();
            document.ExchangeRecords = ledger
                .Where(row => row.Amount < 0 && IsExchangeReason(row.Reason))
                .Select(row => new ExchangeRecord
                {
                    Id = row.Id.ToString(CultureInfo.InvariantCulture),
                    Title = FormatLedgerReason(row.Reason),
                    Points = Math.Abs(row.Amount),
                    CreatedAt = row.CreatedAt ?? DateTime.UtcNow,
                    StatusText = "已兑换",
                })
                .ToList();
            document.InviteRecords = ledger
                .Where(row => row.Amount > 0 && IsInviteReason(row.Reason))
                .Select(row => new InviteRewardRecord
                {
                    Id = row.Id.ToString(CultureInfo.InvariantCulture),
                    Title = "邀请好友奖励",
                    Points = row.Amount,
                    CreatedAt = row.CreatedAt ?? DateTime.UtcNow,
                })
                .ToList();
            document.SuccessfulInviteCount = document.InviteRecords.Count;
            document.InvitedCount = document.SuccessfulInviteCount;
            document.PendingCheckinInviteCount = 0;
            document.InvitePoints = document.InviteRecords.Sum(record => record.Points);

            var tasks = BuildTaskItems(taskDefinitions, progressRows, todayCommentCount, todayReadProgress);
            document.DailyTasks = tasks.Where(task => task.Category == "daily").Select(task => task.Item).ToList();
            document.OneTimeTasks = tasks.Where(task => task.Category == "once").Select(task => task.Item).ToList();
            document.LongTermTasks = tasks.Where(task => task.Category == "long").Select(task => task.Item).ToList();
            if (document.DailyTasks.Count == 0)
            {
                document.DailyTasks = BuildDefaultDailyTasks(todayCommentCount, todayReadProgress);
            }
            if (document.OneTimeTasks.Count == 0)
            {
                document.OneTimeTasks = BuildDefaultOneTimeTasks(progressRows);
            }
            if (document.LongTermTasks.Count == 0)
            {
                document.LongTermTasks = BuildDefaultLongTermTasks(progressRows);
            }

            document.StoreItems = BuildPointStoreItems(products, document.Points);
            return document;
        }

        private static List<(string Category, TaskCenterTaskItem Item)> BuildTaskItems(
            List<RawTaskDefinitionRecord> definitions,
            List<RawUserTaskProgressRecord> progressRows,
            int todayCommentCount,
            int todayReadProgress)
        {
            var periodKey = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var progressByTask = progressRows
                .GroupBy(row => row.TaskId ?? "")
                .ToDictionary(group => group.Key, group => group.ToList());
            var items = new List<(string Category, TaskCenterTaskItem Item)>();

            foreach (var definition in definitions.OrderBy(row => row.SortOrder ?? 0))
            {
                var title = string.IsNullOrWhiteSpace(definition.Title)
                    ? $"任务 {definition.Id}"
                    : definition.Title!;
                var template = ResolveTaskTemplate(title, definition.TaskType, definition.Category);
                // 签到任务有专属的「每日签到」区块,不并入任务列表,避免重复展示。
                if (template.Kind == "signin")
                {
                    continue;
                }

                var hasProgress = progressByTask.TryGetValue(definition.Id ?? "", out var progress) &&
                    progress.Any(row =>
                        string.IsNullOrWhiteSpace(row.PeriodKey) ||
                        string.Equals(row.PeriodKey, periodKey, StringComparison.OrdinalIgnoreCase) ||
                        template.Category != "daily");
                var current = template.Kind switch
                {
                    "read" => Math.Min(todayReadProgress, template.Target),
                    "comment" => Math.Min(todayCommentCount, template.Target),
                    _ => hasProgress ? template.Target : 0,
                };

                items.Add((template.Category, new TaskCenterTaskItem
                {
                    Id = definition.Id ?? "",
                    Title = title,
                    Description = string.IsNullOrWhiteSpace(definition.Description)
                        ? template.Description
                        : definition.Description!,
                    RewardPoints = template.RewardPoints,
                    Current = current,
                    Target = template.Target,
                    IsCompleted = current >= template.Target || hasProgress,
                    IsClaimed = hasProgress,
                    IsRepeatable = template.IsRepeatable,
                    IconGlyph = template.IconGlyph,
                }));
            }

            return items;
        }

        private static List<TaskCenterTaskItem> BuildDefaultDailyTasks(int todayCommentCount, int todayReadProgress)
            =>
            [
                new TaskCenterTaskItem
                {
                    Id = "daily-read",
                    Title = "每日阅读",
                    Description = todayReadProgress >= 20 ? "今日阅读任务已完成" : $"再读 {20 - todayReadProgress} 页得 25 积分",
                    RewardPoints = 25,
                    Current = todayReadProgress,
                    Target = 20,
                    IsCompleted = todayReadProgress >= 20,
                    IconGlyph = "\uE8F1",
                },
                new TaskCenterTaskItem
                {
                    Id = "daily-comment",
                    Title = "每日评论",
                    Description = todayCommentCount >= 2 ? "今日评论任务已完成" : $"再留 {2 - todayCommentCount} 条得 15 积分",
                    RewardPoints = 15,
                    Current = todayCommentCount,
                    Target = 2,
                    IsCompleted = todayCommentCount >= 2,
                    IconGlyph = "\uE8F2",
                },
            ];

        private static List<TaskCenterTaskItem> BuildDefaultOneTimeTasks(List<RawUserTaskProgressRecord> progressRows)
        {
            var isDone = progressRows.Any(row => row.TaskId == "verify_email");
            return
            [
                new TaskCenterTaskItem
                {
                    Id = "verify-email",
                    Title = "验证邮箱",
                    Description = "验证你的邮箱以解锁邀请奖励等功能",
                    RewardPoints = 100,
                    Current = isDone ? 1 : 0,
                    Target = 1,
                    IsCompleted = isDone,
                    IsClaimed = isDone,
                    IconGlyph = "\uE73E",
                },
            ];
        }

        private static List<TaskCenterTaskItem> BuildDefaultLongTermTasks(List<RawUserTaskProgressRecord> progressRows)
        {
            var isDone = progressRows.Any(row => row.TaskId == "invite_friend");
            return
            [
                new TaskCenterTaskItem
                {
                    Id = "invite-friend",
                    Title = "邀请好友",
                    Description = "完成 1 次得 800 积分",
                    RewardPoints = 800,
                    Current = isDone ? 1 : 0,
                    Target = 1,
                    IsCompleted = isDone,
                    IsClaimed = isDone,
                    IsRepeatable = true,
                    IconGlyph = "\uE8F8",
                },
            ];
        }

        private static (string Kind, string Category, int RewardPoints, int Target, bool IsRepeatable, string IconGlyph, string Description)
            ResolveTaskTemplate(string title, string? taskType, string? category)
        {
            var key = $"{title} {taskType} {category}".ToLowerInvariant();
            if (key.Contains("read", StringComparison.Ordinal) || key.Contains("阅读", StringComparison.Ordinal))
            {
                return ("read", "daily", 25, 20, false, "\uE8F1", "阅读 20 页可获得积分");
            }

            if (key.Contains("comment", StringComparison.Ordinal) || key.Contains("评论", StringComparison.Ordinal))
            {
                return ("comment", "daily", 15, 2, false, "\uE8F2", "留下 2 条评论可获得积分");
            }

            if (key.Contains("sign", StringComparison.Ordinal) ||
                key.Contains("check", StringComparison.Ordinal) ||
                key.Contains("签到", StringComparison.Ordinal))
            {
                return ("signin", "daily", 20, 1, false, "\uE787", "完成每日签到可获得积分");
            }

            if (key.Contains("invite", StringComparison.Ordinal) || key.Contains("邀请", StringComparison.Ordinal))
            {
                return ("invite", "long", 800, 1, true, "\uE8F8", "邀请好友可获得积分");
            }

            if (key.Contains("email", StringComparison.Ordinal) || key.Contains("邮箱", StringComparison.Ordinal))
            {
                return ("email", "once", 100, 1, false, "\uE73E", "验证邮箱可获得积分");
            }

            if (key.Contains("once", StringComparison.Ordinal) || key.Contains("一次", StringComparison.Ordinal))
            {
                return ("custom", "once", 10, 1, false, "\uE73E", "完成任务可获得积分");
            }

            if (key.Contains("long", StringComparison.Ordinal) || key.Contains("长期", StringComparison.Ordinal))
            {
                return ("custom", "long", 10, 1, true, "\uE8F8", "完成任务可获得积分");
            }

            return ("custom", "daily", 10, 1, false, "\uE8F1", "完成任务可获得积分");
        }

        private static List<PointStoreItem> BuildPointStoreItems(List<RawPointProductRecord> products, int availablePoints)
        {
            var items = products
                .Where(product => product.IsActive != false &&
                    string.Equals(product.Type, "subscription", StringComparison.OrdinalIgnoreCase))
                .OrderBy(product => product.SortOrder ?? 0)
                .Select(product =>
                {
                    var days = Math.Max(1, product.DurationDays ?? 1);
                    var points = EstimateVipPointCost(days, product.Price);
                    return new PointStoreItem
                    {
                        Id = string.IsNullOrWhiteSpace(product.Id) ? $"vip-{days}" : product.Id!,
                        Category = "virtual",
                        Title = string.IsNullOrWhiteSpace(product.Name) ? $"{days} 天 VIP" : product.Name!,
                        Description = $"购买 {days} 天 VIP 会员",
                        Points = points,
                        Price = product.Price,
                        DurationDays = days,
                        Stock = product.StockLimit is { } stockLimit
                            ? Math.Max(0, stockLimit - (product.SalesCount ?? 0))
                            : 0,
                        ImageUrl = product.ImageUrl,
                        IconGlyph = "\uE7BF",
                        AvailablePoints = availablePoints,
                    };
                })
                .ToList();

            if (items.Count == 0)
            {
                items.Add(new PointStoreItem
                {
                    Id = "vip-7d",
                    Category = "virtual",
                    Title = "7 天 VIP",
                    Description = "使用 800 积分兑换 7 天 VIP 会员",
                    Points = 800,
                    IconGlyph = "\uE7BF",
                    AvailablePoints = availablePoints,
                });
                items.Add(new PointStoreItem
                {
                    Id = "vip-1d",
                    Category = "virtual",
                    Title = "1 天 VIP",
                    Description = "使用 250 积分兑换 1 天 VIP 会员",
                    Points = 250,
                    IconGlyph = "\uE7BF",
                    AvailablePoints = availablePoints,
                });
            }

            items.AddRange(BuildPhysicalRewardItems(availablePoints));
            return items;
        }

        private static IEnumerable<PointStoreItem> BuildPhysicalRewardItems(int availablePoints)
        {
            return
            [
                new PointStoreItem
                {
                    Id = "acrylic-charm",
                    Category = "physical",
                    Title = "亚克力挂件",
                    Description = "使用 1200 积分兑换一枚亚克力挂件",
                    Points = 1200,
                    Stock = 997,
                    IconGlyph = "\uE7C3",
                    AvailablePoints = availablePoints,
                },
                new PointStoreItem
                {
                    Id = "towel",
                    Category = "physical",
                    Title = "麻薯素毛毯",
                    Description = "使用 4000 积分兑换三袋麻薯素毛毯",
                    Points = 4000,
                    Stock = 999,
                    IconGlyph = "\uE790",
                    AvailablePoints = availablePoints,
                },
                new PointStoreItem
                {
                    Id = "cookies",
                    Category = "physical",
                    Title = "曲奇饼干",
                    Description = "使用 8000 积分兑换三袋曲奇饼干",
                    Points = 8000,
                    Stock = 998,
                    IconGlyph = "\uE7C1",
                    AvailablePoints = availablePoints,
                },
                new PointStoreItem
                {
                    Id = "desk-pad",
                    Category = "physical",
                    Title = "动漫鼠标垫",
                    Description = "使用 12000 积分兑换一张动漫鼠标垫",
                    Points = 12000,
                    Stock = 996,
                    IconGlyph = "\uE8A7",
                    AvailablePoints = availablePoints,
                },
                new PointStoreItem
                {
                    Id = "food-box",
                    Category = "physical",
                    Title = "哈基米南北绿豆浆",
                    Description = "使用 13800 积分兑换一箱限定饮品",
                    Points = 13800,
                    Stock = 998,
                    IconGlyph = "\uE8D4",
                    AvailablePoints = availablePoints,
                },
                new PointStoreItem
                {
                    Id = "figure-custom",
                    Category = "physical",
                    Title = "128 元内手办自选",
                    Description = "使用 80000 积分兑换 128 元以内的手办",
                    Points = 80000,
                    Stock = 998,
                    IconGlyph = "\uE7C3",
                    AvailablePoints = availablePoints,
                },
            ];
        }

        private static int EstimateVipPointCost(int durationDays, decimal price)
        {
            if (durationDays <= 1) return 250;
            if (durationDays <= 7) return 800;
            var fromDays = durationDays * 220;
            var fromPrice = (int)Math.Round(price * 1000m, MidpointRounding.AwayFromZero);
            return Math.Max(fromDays, fromPrice);
        }

        private static void ApplyCheckinPreview(TaskCenterDocument document, RawCheckinWeekPreview preview)
        {
            var culture = new CultureInfo("zh-CN");
            var weekdayLabels = new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
            var days = new List<TaskCenterSignInDay>();
            RawCheckinDay? todayDay = null;

            foreach (var day in preview.Days!)
            {
                var isToday = !string.IsNullOrWhiteSpace(day.Date) &&
                    string.Equals(day.Date, preview.Today, StringComparison.Ordinal);
                if (isToday)
                {
                    todayDay = day;
                }

                string label;
                if (DateTime.TryParse(day.Date, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var parsedDate))
                {
                    label = culture.DateTimeFormat.GetAbbreviatedDayName(parsedDate.DayOfWeek);
                }
                else
                {
                    label = day.DayOfWeek is >= 1 and <= 7 ? weekdayLabels[day.DayOfWeek - 1] : "";
                }

                days.Add(new TaskCenterSignInDay
                {
                    Label = label,
                    Points = day.Points,
                    IsChecked = IsCheckedState(day.State),
                    IsToday = isToday,
                });
            }

            document.SignInDays = days;
            document.HasSignedInToday = todayDay != null && IsCheckedState(todayDay.State);
            // RPC 的 streak 是「当天签到后」会达到的连续天数:今天已签即为当前连续天数,
            // 今天未签则当前连续天数为该值减 1。
            document.SignInStreak = todayDay == null
                ? 0
                : Math.Max(0, document.HasSignedInToday ? todayDay.Streak : todayDay.Streak - 1);
        }

        private static bool IsCheckedState(string? state)
        {
            if (string.IsNullOrWhiteSpace(state)) return false;
            // get_checkin_week_preview 用 *_claimed 表示该日已签到(如 today_claimed / past_claimed)。
            var normalized = state.ToLowerInvariant();
            return normalized.Contains("claimed", StringComparison.Ordinal) ||
                normalized.Contains("checked", StringComparison.Ordinal) ||
                normalized.Contains("signed", StringComparison.Ordinal) ||
                normalized.Contains("done", StringComparison.Ordinal);
        }

        private static List<TaskCenterSignInDay> BuildSignInDays(
            List<RawPointLedgerRecord> ledger,
            DateTime today)
        {
            var culture = new CultureInfo("zh-CN");
            var defaults = new[] { 10, 15, 20, 10, 15, 15, 20 };
            var start = today.AddDays(-6);
            var result = new List<TaskCenterSignInDay>();

            for (var i = 0; i < 7; i++)
            {
                var date = start.AddDays(i);
                var signedRows = ledger.Where(row =>
                    IsSignInReason(row.Reason) &&
                    ToLocalDate(row.CreatedAt) == date)
                    .ToList();
                var points = signedRows.Where(row => row.Amount > 0).Sum(row => row.Amount);
                result.Add(new TaskCenterSignInDay
                {
                    Label = culture.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek),
                    Points = points > 0 ? points : defaults[i],
                    IsChecked = signedRows.Count > 0,
                    IsToday = date == today,
                });
            }

            return result;
        }

        private static int CalculateSignInStreak(List<RawPointLedgerRecord> ledger, DateTime today)
        {
            var streak = 0;
            for (var date = today; date >= today.AddDays(-30); date = date.AddDays(-1))
            {
                if (!ledger.Any(row => IsSignInReason(row.Reason) && ToLocalDate(row.CreatedAt) == date))
                {
                    break;
                }

                streak++;
            }

            return streak;
        }

        private static DateTime? ToLocalDate(DateTime? time)
        {
            if (time == null) return null;
            return (time.Value.Kind == DateTimeKind.Utc ? time.Value.ToLocalTime() : time.Value).Date;
        }

        private static bool IsSignInReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;
            var normalized = reason.ToLowerInvariant();
            return normalized.Contains("sign", StringComparison.Ordinal) ||
                normalized.Contains("check", StringComparison.Ordinal) ||
                normalized.Contains("签到", StringComparison.Ordinal);
        }

        private static bool IsExchangeReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;
            var normalized = reason.ToLowerInvariant();
            return normalized.Contains("exchange", StringComparison.Ordinal) ||
                normalized.Contains("redeem", StringComparison.Ordinal) ||
                normalized.Contains("兑换", StringComparison.Ordinal);
        }

        private static bool IsInviteReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;
            var normalized = reason.ToLowerInvariant();
            return normalized.Contains("invite", StringComparison.Ordinal) ||
                normalized.Contains("邀请", StringComparison.Ordinal);
        }

        private static string FormatLedgerReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "积分变动";

            var normalized = reason.Trim().ToLowerInvariant();
            if (normalized.Contains("read", StringComparison.Ordinal) || normalized.Contains("阅读", StringComparison.Ordinal))
            {
                return "每日阅读";
            }
            if (normalized.Contains("comment", StringComparison.Ordinal) || normalized.Contains("评论", StringComparison.Ordinal))
            {
                return "每日评论";
            }
            if (normalized.Contains("sign", StringComparison.Ordinal) ||
                normalized.Contains("check", StringComparison.Ordinal) ||
                normalized.Contains("签到", StringComparison.Ordinal))
            {
                return "每日签到";
            }
            if (normalized.Contains("email", StringComparison.Ordinal) || normalized.Contains("邮箱", StringComparison.Ordinal))
            {
                return "验证邮箱";
            }
            if (normalized.Contains("invite", StringComparison.Ordinal) || normalized.Contains("邀请", StringComparison.Ordinal))
            {
                return "邀请好友";
            }
            if (normalized.Contains("exchange", StringComparison.Ordinal) ||
                normalized.Contains("redeem", StringComparison.Ordinal) ||
                normalized.Contains("兑换", StringComparison.Ordinal))
            {
                return "积分兑换";
            }

            return reason.Trim();
        }

        private static string FormatRelativeTime(DateTime? time)
        {
            if (time == null) return "";
            var localTime = time.Value.Kind == DateTimeKind.Utc
                ? time.Value.ToLocalTime()
                : time.Value;
            var elapsed = DateTime.Now - localTime;

            if (elapsed.TotalMinutes < 1) return "刚刚";
            if (elapsed.TotalHours < 1) return $"{Math.Max(1, (int)elapsed.TotalMinutes)} 分钟前";
            if (elapsed.TotalDays < 1) return $"{Math.Max(1, (int)elapsed.TotalHours)} 小时前";
            if (elapsed.TotalDays < 7) return $"{Math.Max(1, (int)elapsed.TotalDays)} 天前";

            return localTime.ToString("yyyy-MM-dd");
        }

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
            request.Headers.TryAddWithoutValidation("Accept-Profile", "public");

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

        private async Task<List<T>> GetRestRecordsWithOptionalAuthAsync<T>(string relativePath)
        {
            var token = CurrentSession?.AccessToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                return await GetRestRecordsAsync<T>(relativePath);
            }

            return await SendRestAsync<List<T>>(
                HttpMethod.Get,
                relativePath,
                token,
                content: null,
                parseList: true) ?? new List<T>();
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
            request.Headers.TryAddWithoutValidation("Accept-Profile", "public");
            request.Headers.TryAddWithoutValidation("Content-Profile", "public");
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
            var token = CurrentSession?.AccessToken;
            var userId = CurrentUserId;
            if (string.IsNullOrWhiteSpace(token) ||
                string.IsNullOrWhiteSpace(userId))
            {
                throw new InvalidOperationException(message);
            }

            return userId!;
        }

        private static string? ReadJwtSubject(string? accessToken)
            => ReadJwtClaim(accessToken, "sub");

        private static string? ReadJwtClaim(string? accessToken, string claimName)
        {
            if (string.IsNullOrWhiteSpace(accessToken)) return null;
            if (string.IsNullOrWhiteSpace(claimName)) return null;

            try
            {
                var parts = accessToken.Split('.');
                if (parts.Length < 2) return null;

                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');

                var json = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                return json.Value<string>(claimName);
            }
            catch
            {
                return null;
            }
        }

        public async Task<PaymentOrder> CreatePaymentOrderAsync(
            string productId,
            string payType = "wxpay")
        {
            if (string.IsNullOrWhiteSpace(productId))
            {
                throw new ArgumentException("商品 ID 不能为空。", nameof(productId));
            }

            var body = new Dictionary<string, object>
            {
                ["product_id"] = productId,
                ["quantity"] = 1,
                ["pay_type"] = string.IsNullOrWhiteSpace(payType) ? "wxpay" : payType,
                ["device"] = "pc",
            };

            var response = await InvokePaymentFunctionAsync<RawCreateOrderResponse>("create-order", body);
            if (response?.Success != true || response.Data == null)
            {
                throw new InvalidOperationException(response?.Error ?? response?.Message ?? "创建订单失败,请稍后重试。");
            }

            return MapPaymentOrder(response.Data);
        }

        public async Task<PaymentOrder> QueryPaymentOrderAsync(
            string? orderId = null,
            string? tradeNo = null,
            bool syncHypay = false)
        {
            if (string.IsNullOrWhiteSpace(orderId) && string.IsNullOrWhiteSpace(tradeNo))
            {
                throw new ArgumentException("订单 ID 和订单号不能同时为空。");
            }

            var body = new Dictionary<string, object>
            {
                ["sync_hypay"] = syncHypay,
            };
            if (!string.IsNullOrWhiteSpace(orderId))
            {
                body["order_id"] = orderId;
            }
            if (!string.IsNullOrWhiteSpace(tradeNo))
            {
                body["trade_no"] = tradeNo;
            }

            var response = await InvokePaymentFunctionAsync<RawQueryOrderResponse>("query-order", body);
            if (response?.Success != true || response.Data == null)
            {
                throw new InvalidOperationException(response?.Error ?? response?.Message ?? "查询订单失败,请稍后重试。");
            }

            return MapPaymentOrder(response.Data);
        }

        private ComicComment MapComment(RawCommentRecord record, RawProfileSummaryRecord? profile)
        {
            var displayName = profile?.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = profile?.Username;
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = "花火用户";
            }

            var userId = record.UserId ?? "";
            return new ComicComment
            {
                Id = record.Id,
                ComicId = record.ComicId,
                ChapterId = record.ChapterId,
                UserId = userId,
                Content = record.Content ?? "",
                Status = string.IsNullOrWhiteSpace(record.Status) ? "public" : record.Status!,
                IsSpoiler = record.IsSpoiler == true,
                LikeCount = record.LikeCount ?? 0,
                ReplyCount = record.ReplyCount ?? 0,
                CreatedAt = record.CreatedAt,
                UpdatedAt = record.UpdatedAt,
                DisplayName = displayName,
                Username = profile?.Username,
                AvatarUrl = profile?.AvatarUrl,
                IsMine = !string.IsNullOrWhiteSpace(CurrentUser?.Id) &&
                    string.Equals(CurrentUser!.Id, userId, StringComparison.Ordinal),
            };
        }

        private static string GetCommentStatusLabel(string? status) => status switch
        {
            "pending" => "审核中",
            "shadow_banned" => "仅自己可见",
            "rejected" => "未通过",
            _ => "评论",
        };

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

        private static ComicListItem MapFilteredComic(RawRankingComicRecord record)
        {
            var status = record.IsFinished ? "完结" : "连载中";
            var rating = record.RatingCount is > 0
                ? $"评分 {record.RatingAverage ?? 0:0.0}"
                : "暂无评分";

            return new ComicListItem
            {
                Id = record.Id.ToString(CultureInfo.InvariantCulture),
                ComicId = record.Id,
                Title = record.Title ?? "",
                Subtitle = $"{status} · {rating}",
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
