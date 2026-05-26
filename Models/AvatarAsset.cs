using System;
using System.Collections.Generic;
using System.IO;

namespace hanabimanga.Models
{
    // 头像解析：把后端的 avatar_url 统一成可绑定的图片源。
    // 评论、反馈工单、共鸣用户列表共用，避免逻辑分散。
    internal static class AvatarAsset
    {
        private const string DefaultAsset = "ms-appx:///Assets/avatar/ic_avatar_default.webp";

        // 与 SupabaseService.BuildAvatarPresetOptions 保持一致的 16 个预设文件。
        private static readonly HashSet<string> KnownPresets;

        static AvatarAsset()
        {
            KnownPresets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ic_avatar_default.webp",
            };
            for (var i = 1; i <= 15; i++)
            {
                KnownPresets.Add($"ic_avatar_{i:00}.webp");
            }
        }

        // null/空 → 默认；绝对 URL → 原样返回；
        // 其它 → 取文件名、补 .webp、校验为已知预设，否则回退默认。
        public static string ResolvePreviewUrl(string? avatarUrl)
        {
            if (string.IsNullOrWhiteSpace(avatarUrl))
            {
                return DefaultAsset;
            }

            if (Uri.TryCreate(avatarUrl, UriKind.Absolute, out _))
            {
                return avatarUrl;
            }

            var fileName = Path.GetFileName(avatarUrl.Trim().Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return DefaultAsset;
            }

            if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
            {
                fileName += ".webp";
            }

            return KnownPresets.Contains(fileName)
                ? $"ms-appx:///Assets/avatar/{fileName}"
                : DefaultAsset;
        }
    }
}
