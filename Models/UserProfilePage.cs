using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace hanabimanga.Models
{
    public sealed class UserProfileDocument
    {
        public UserProfileHeader Profile { get; set; } = new();
        public List<UserTimelineItem> Timeline { get; set; } = new();
    }

    public sealed class UserProfileHeader
    {
        public string UserId { get; set; } = "";
        public string? Username { get; set; }
        public string DisplayName { get; set; } = "花火用户";
        public string? AvatarUrl { get; set; }
        public string? BannerUrl { get; set; }
        public DateTime? CreatedAt { get; set; }
        public bool IsSelf { get; set; }
        public bool IsEmailVerified { get; set; }
        public int CommentCount { get; set; }
        public int FavoriteCount { get; set; }
        public int LikeCount { get; set; }

        public string AvatarPreviewUrl => ToAssetOrAbsoluteUrl(AvatarUrl, "avatar", "ic_avatar_default.webp");
        public string BannerPreviewUrl => ToAssetOrAbsoluteUrl(BannerUrl, "banner", "ic_banner_default.webp");
        public string UsernameText => string.IsNullOrWhiteSpace(Username) ? $"ID {ShortUserId}" : $"@{Username}";
        public string JoinedText => CreatedAt is { } createdAt
            ? $"加入于 {createdAt.ToLocalTime():yyyy-MM-dd}"
            : "";
        public string StatsText => $"{CommentCount} 评论 · {FavoriteCount} 收藏 · {LikeCount} 点赞";

        private string ShortUserId => UserId.Length <= 8 ? UserId : UserId[..8];

        internal static string ToAssetOrAbsoluteUrl(string? value, string folder, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return $"ms-appx:///Assets/{folder}/{fallback}";
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out _))
            {
                return value;
            }

            var fileName = Path.GetFileName(value.Trim().Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return $"ms-appx:///Assets/{folder}/{fallback}";
            }

            if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
            {
                fileName += ".webp";
            }

            return $"ms-appx:///Assets/{folder}/{fileName}";
        }
    }

    public sealed class UserTimelineItem
    {
        public string Kind { get; set; } = "";
        public string IconGlyph { get; set; } = "\uE8F2";
        public string Title { get; set; } = "";
        public string? Body { get; set; }
        public string? Meta { get; set; }
        public DateTime? CreatedAt { get; set; }
        public long? ComicId { get; set; }
        public string? ComicTitle { get; set; }
        public string? ComicCoverUrl { get; set; }

        public bool HasBody => !string.IsNullOrWhiteSpace(Body);
        public bool HasComic => ComicId is > 0 && !string.IsNullOrWhiteSpace(ComicTitle);
        public string CreatedAtText => FormatCreatedAt(CreatedAt);
        public string NavigationComicId => ComicId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";

        private static string FormatCreatedAt(DateTime? createdAt)
        {
            if (createdAt == null) return "";

            var localTime = createdAt.Value.Kind == DateTimeKind.Utc
                ? createdAt.Value.ToLocalTime()
                : createdAt.Value;
            var elapsed = DateTime.Now - localTime;

            if (elapsed.TotalMinutes < 1) return "刚刚";
            if (elapsed.TotalHours < 1) return $"{Math.Max(1, (int)elapsed.TotalMinutes)} 分钟前";
            if (elapsed.TotalDays < 1) return $"{Math.Max(1, (int)elapsed.TotalHours)} 小时前";
            if (elapsed.TotalDays < 7) return $"{Math.Max(1, (int)elapsed.TotalDays)} 天前";

            return localTime.ToString("yyyy-MM-dd");
        }
    }

    internal sealed class RawUserProfileRecord
    {
        [JsonProperty("id")]
        public string? Id { get; set; }

        [JsonProperty("username")]
        public string? Username { get; set; }

        [JsonProperty("display_name")]
        public string? DisplayName { get; set; }

        [JsonProperty("avatar_url")]
        public string? AvatarUrl { get; set; }

        [JsonProperty("banner_url")]
        public string? BannerUrl { get; set; }

        [JsonProperty("email_verified_at")]
        public DateTime? EmailVerifiedAt { get; set; }

        [JsonProperty("created_at")]
        public DateTime? CreatedAt { get; set; }
    }

    internal sealed class RawUserRatingTimelineRecord
    {
        [JsonProperty("comic_id")]
        public long ComicId { get; set; }

        [JsonProperty("score")]
        public int Score { get; set; }

        [JsonProperty("updated_at")]
        public DateTime? UpdatedAt { get; set; }

        [JsonProperty("created_at")]
        public DateTime? CreatedAt { get; set; }
    }
}
