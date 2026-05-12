using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace hanabimanga.Models
{
    // ============= UI / 内部模型 =============

    public class HomeFeedResponse
    {
        public List<HomeFeedBanner> Banners { get; set; } = new();
        public List<HomeFeedSection> Sections { get; set; } = new();
    }

    public class HomeFeedBanner
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; }
        public string? ImageUrl { get; set; }
        public string? LinkUrl { get; set; }
    }

    public class HomeFeedSection
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public List<HomeFeedItem> Items { get; set; } = new();
    }

    public class HomeFeedItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Author { get; set; }
        public string? CoverUrl { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    // ============= Edge function 原始响应模型 =============

    internal class RawHomeFeed
    {
        [JsonProperty("data")]
        public RawHomeFeedData? Data { get; set; }
    }

    internal class RawHomeFeedData
    {
        [JsonProperty("recommended")]
        public List<RawMangaItem>? Recommended { get; set; }

        [JsonProperty("recent")]
        public List<RawMangaItem>? Recent { get; set; }

        [JsonProperty("popular")]
        public RawPopular? Popular { get; set; }

        [JsonProperty("newlyAdded")]
        public List<RawMangaItem>? NewlyAdded { get; set; }

        [JsonProperty("banners")]
        public List<RawBanner>? Banners { get; set; }
    }

    internal class RawPopular
    {
        [JsonProperty("daily")]
        public List<RawMangaItem>? Daily { get; set; }

        [JsonProperty("weekly")]
        public List<RawMangaItem>? Weekly { get; set; }

        [JsonProperty("monthly")]
        public List<RawMangaItem>? Monthly { get; set; }
    }

    internal class RawMangaItem
    {
        [JsonProperty("documentId")]
        public string? DocumentId { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("cover")]
        public string? Cover { get; set; }

        [JsonProperty("poster")]
        public string? Poster { get; set; }

        [JsonProperty("categoryName")]
        public string? CategoryName { get; set; }

        [JsonProperty("latestChapterTitle")]
        public string? LatestChapterTitle { get; set; }

        [JsonProperty("latestChapterUpdatedAt")]
        public DateTime? LatestChapterUpdatedAt { get; set; }
    }

    internal class RawBanner
    {
        [JsonProperty("id")]
        public string? Id { get; set; }

        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("imageUrl")]
        public string? ImageUrl { get; set; }

        [JsonProperty("linkUrl")]
        public string? LinkUrl { get; set; }
    }
}
