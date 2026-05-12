using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using hanabimanga.Models;
using Newtonsoft.Json;
using Supabase;
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
        private readonly SemaphoreSlim _initLock = new(1, 1);

        public Client Client =>
            _client ?? throw new InvalidOperationException(
                "SupabaseService 尚未初始化,请先调用 InitializeAsync。");

        public Session? CurrentSession => _client?.Auth.CurrentSession;
        public User? CurrentUser => _client?.Auth.CurrentUser;
        public bool IsInitialized => _client is not null;

        private SupabaseService() { }

        public async Task InitializeAsync(string url, string anonKey, SupabaseOptions? options = null)
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

                var client = new Client(url, anonKey, options);
                await client.InitializeAsync();
                _client = client;
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
    }
}
