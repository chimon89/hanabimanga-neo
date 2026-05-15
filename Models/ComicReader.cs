using System.Collections.Generic;
using Newtonsoft.Json;

namespace hanabimanga.Models
{
    public sealed class ComicReaderNavigationParameter
    {
        public long ComicId { get; set; }
        public long ChapterId { get; set; }
        public int StartPage { get; set; } = 1;
    }

    public sealed class ComicReaderDocument
    {
        public long ComicId { get; set; }
        public string ComicTitle { get; set; } = "";
        public ComicChapter Chapter { get; set; } = new();
        public ComicChapter? PreviousChapter { get; set; }
        public ComicChapter? NextChapter { get; set; }
        public int TotalChapters { get; set; }
        public List<ReaderPageImage> Pages { get; set; } = new();
        public string PlatformRouted { get; set; } = "";
        public bool UsesWebFallback => PlatformRouted == "web";

        // 是否有高清(AI 超分)版本可选(来自 comics.has_upscaled)
        public bool HasUpscaled { get; set; }

        // 本次资源是从 vip-image-url 取得(true)还是 sd-image-url(false)
        public bool IsUpscaled { get; set; }

        // 非 VIP 用户调用 vip-image-url 时,服务端会返回每日免费配额信息
        public ImageQuota? Quota { get; set; }
    }

    public sealed class ImageQuota
    {
        public int UsedToday { get; set; }
        public int Remaining { get; set; }
        public int DailyLimit { get; set; }
        public bool IsVip { get; set; }
    }

    public sealed class ReaderPageImage
    {
        public int PageNumber { get; set; }
        public string PageLabel => PageNumber.ToString("000");
        public string Url { get; set; } = "";
    }

    internal sealed class RawReaderImageResponse
    {
        [JsonProperty("urls")]
        public List<RawReaderImageUrl>? Urls { get; set; }

        [JsonProperty("metadata")]
        public RawReaderMetadata? Metadata { get; set; }

        // 仅 vip-image-url 给非 VIP 用户时返回
        [JsonProperty("quota")]
        public RawReaderQuota? Quota { get; set; }

        [JsonProperty("error")]
        public string? Error { get; set; }

        [JsonProperty("message")]
        public string? Message { get; set; }
    }

    internal sealed class RawReaderQuota
    {
        [JsonProperty("usedToday")]
        public int UsedToday { get; set; }

        [JsonProperty("remaining")]
        public int Remaining { get; set; }

        [JsonProperty("dailyLimit")]
        public int DailyLimit { get; set; }

        [JsonProperty("isVip")]
        public bool IsVip { get; set; }
    }

    internal sealed class RawPremiumQuota
    {
        [JsonProperty("usedToday")]
        public int UsedToday { get; set; }

        [JsonProperty("dailyLimit")]
        public int DailyLimit { get; set; }

        [JsonProperty("remaining")]
        public int Remaining { get; set; }

        [JsonProperty("isVip")]
        public bool IsVip { get; set; }
    }

    internal sealed class RawReaderImageUrl
    {
        [JsonProperty("page")]
        public string? Page { get; set; }

        [JsonProperty("url")]
        public string? Url { get; set; }
    }

    internal sealed class RawReaderMetadata
    {
        [JsonProperty("platformRouted")]
        public string? PlatformRouted { get; set; }

        [JsonProperty("totalPages")]
        public int? TotalPages { get; set; }
    }
}
