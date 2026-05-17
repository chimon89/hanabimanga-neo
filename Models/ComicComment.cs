using System;
using System.IO;
using Newtonsoft.Json;

namespace hanabimanga.Models
{
    public sealed class ComicCommentNavigationParameter
    {
        public long ComicId { get; set; }
        public string ComicTitle { get; set; } = "";
    }

    public sealed class ComicComment
    {
        public long Id { get; set; }
        public long ComicId { get; set; }
        public long? ChapterId { get; set; }
        public string UserId { get; set; } = "";
        public string Content { get; set; } = "";
        public string Status { get; set; } = "public";
        public bool IsSpoiler { get; set; }
        public int LikeCount { get; set; }
        public int ReplyCount { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string DisplayName { get; set; } = "花火用户";
        public string? Username { get; set; }
        public string? AvatarUrl { get; set; }
        public bool IsMine { get; set; }

        public string AvatarPreviewUrl => ToAvatarPreviewUrl(AvatarUrl);
        public string CreatedAtText => FormatCreatedAt(CreatedAt);
        public bool HasStatusNote => Status != "public";
        public string StatusText => Status switch
        {
            "pending" => "审核中",
            "shadow_banned" => IsMine ? "仅自己可见" : "审核隐藏",
            "rejected" => "未通过",
            _ => "",
        };
        public bool HasMetaText => LikeCount > 0 || ReplyCount > 0 || IsSpoiler;
        public string MetaText
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                if (IsSpoiler) parts.Add("剧透");
                if (LikeCount > 0) parts.Add($"{LikeCount} 赞");
                if (ReplyCount > 0) parts.Add($"{ReplyCount} 回复");
                return string.Join(" · ", parts);
            }
        }

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

        private static string ToAvatarPreviewUrl(string? avatarUrl)
        {
            if (string.IsNullOrWhiteSpace(avatarUrl))
            {
                return "ms-appx:///Assets/avatar/ic_avatar_default.webp";
            }

            if (Uri.TryCreate(avatarUrl, UriKind.Absolute, out _))
            {
                return avatarUrl;
            }

            var fileName = Path.GetFileName(avatarUrl.Trim().Replace('\\', '/'));
            return string.IsNullOrWhiteSpace(fileName)
                ? "ms-appx:///Assets/avatar/ic_avatar_default.webp"
                : $"ms-appx:///Assets/avatar/{fileName}";
        }
    }

    internal sealed class RawCommentRecord
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("user_id")]
        public string? UserId { get; set; }

        [JsonProperty("comic_id")]
        public long ComicId { get; set; }

        [JsonProperty("chapter_id")]
        public long? ChapterId { get; set; }

        [JsonProperty("parent_id")]
        public long? ParentId { get; set; }

        [JsonProperty("content")]
        public string? Content { get; set; }

        [JsonProperty("status")]
        public string? Status { get; set; }

        [JsonProperty("is_spoiler")]
        public bool? IsSpoiler { get; set; }

        [JsonProperty("like_count")]
        public int? LikeCount { get; set; }

        [JsonProperty("reply_count")]
        public int? ReplyCount { get; set; }

        [JsonProperty("created_at")]
        public DateTime? CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public DateTime? UpdatedAt { get; set; }
    }

    internal sealed class RawProfileSummaryRecord
    {
        [JsonProperty("id")]
        public string? Id { get; set; }

        [JsonProperty("username")]
        public string? Username { get; set; }

        [JsonProperty("display_name")]
        public string? DisplayName { get; set; }

        [JsonProperty("avatar_url")]
        public string? AvatarUrl { get; set; }
    }
}
