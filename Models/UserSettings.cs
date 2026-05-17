using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace hanabimanga.Models
{
    public sealed class UserSettingsDocument
    {
        public string Email { get; set; } = "";
        public UserProfile Profile { get; set; } = new();
        public List<UserBadgeItem> Badges { get; set; } = new();
        public List<AvatarPresetOption> AvatarPresets { get; set; } = new();
        public List<BannerPresetOption> BannerPresets { get; set; } = new();
    }

    public sealed class AvatarPresetOption
    {
        public string FileName { get; set; } = "";
        public string Label { get; set; } = "";
        public string PreviewUrl => $"ms-appx:///Assets/avatar/{FileName}";
    }

    public sealed class BannerPresetOption
    {
        public string FileName { get; set; } = "";
        public string Label { get; set; } = "";
        public string PreviewUrl => $"ms-appx:///Assets/banner/{FileName}";
    }

    public sealed class UserBadgeItem
    {
        public long Id { get; set; }
        public long BadgeId { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string? ImageUrl { get; set; }
        public bool IsDisplayed { get; set; }
        public DateTime? ExpiresAt { get; set; }

        public bool IsExpired => ExpiresAt is { } expiresAt && expiresAt <= DateTime.UtcNow;
        public string DisplayStatus => IsExpired
            ? "已过期"
            : IsDisplayed
                ? "正在展示"
                : "未展示";
        public string DisplayActionLabel => IsDisplayed ? "取消展示" : "展示";
        public string ImagePreviewUrl => ToAssetOrAbsoluteUrl(ImageUrl, "badge", "default_cover.webp");

        private static string ToAssetOrAbsoluteUrl(string? value, string folder, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return $"ms-appx:///Assets/{folder}/{fallback}";
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out _))
            {
                return value;
            }

            var fileName = ResolveLocalAssetName(Path.GetFileName(value.Trim().Replace('\\', '/')));
            return string.IsNullOrWhiteSpace(fileName)
                ? $"ms-appx:///Assets/{folder}/{fallback}"
                : $"ms-appx:///Assets/{folder}/{fileName}";
        }

        private static string ResolveLocalAssetName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "";

            var normalized = fileName.Trim();
            var key = Path.GetFileNameWithoutExtension(normalized).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(Path.GetExtension(normalized)))
            {
                return key switch
                {
                    "alpha-test" => "alpha_user_badge.webp",
                    "alpha_user_badge" => "alpha_user_badge.webp",
                    "fr" => "fr.png",
                    _ => $"{normalized}.webp",
                };
            }

            return normalized;
        }
    }

    internal sealed class RawUserBadgeRecord
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("badge_id")]
        public long BadgeId { get; set; }

        [JsonProperty("is_displayed")]
        public bool IsDisplayed { get; set; }

        [JsonProperty("expires_at")]
        public DateTime? ExpiresAt { get; set; }
    }

    internal sealed class RawBadgeDefinitionRecord
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("description")]
        public string? Description { get; set; }

        [JsonProperty("image_url")]
        public string? ImageUrl { get; set; }
    }
}
