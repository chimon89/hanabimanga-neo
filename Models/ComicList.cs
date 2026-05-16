using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace hanabimanga.Models
{
    public sealed class ComicSearchDocument
    {
        public string Query { get; set; } = "";
        public long TotalCount { get; set; }
        public List<ComicListItem> Items { get; set; } = new();
    }

    public sealed class ComicListItem
    {
        public string Id { get; set; } = "";
        public long ComicId { get; set; }
        public long? ChapterId { get; set; }
        public int StartPage { get; set; } = 1;
        public string ReaderKey => ChapterId is { } chapterId ? $"{ComicId}:{chapterId}:{StartPage}" : "";
        public string Title { get; set; } = "";
        public string? Subtitle { get; set; }
        public bool ShowSubtitle { get; set; } = true;
        public string? CoverUrl { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    public enum RankingKind
    {
        Daily,
        Weekly,
        Monthly,
        Rating,
        RatingCount,
    }

    public sealed class RankingComicItem
    {
        public int Rank { get; set; }
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? CoverUrl { get; set; }
        public string? Subtitle { get; set; }
        public string RankText => Rank.ToString();
        public bool IsTopThree => Rank is >= 1 and <= 3;
    }

    internal sealed class RawRankingComicRecord
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("cover_url")]
        public string? CoverUrl { get; set; }

        [JsonProperty("is_finished")]
        public bool IsFinished { get; set; }

        [JsonProperty("rating_average")]
        public double? RatingAverage { get; set; }

        [JsonProperty("rating_count")]
        public int? RatingCount { get; set; }

        [JsonProperty("popularity_daily")]
        public int? PopularityDaily { get; set; }

        [JsonProperty("popularity_weekly")]
        public int? PopularityWeekly { get; set; }

        [JsonProperty("popularity_monthly")]
        public int? PopularityMonthly { get; set; }
    }

    internal sealed class RawComicSearchRecord
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("summary")]
        public string? Summary { get; set; }

        [JsonProperty("cover_url")]
        public string? CoverUrl { get; set; }

        [JsonProperty("rating_average")]
        public double? RatingAverage { get; set; }

        [JsonProperty("rating_count")]
        public int? RatingCount { get; set; }

        [JsonProperty("is_finished")]
        public bool IsFinished { get; set; }

        [JsonProperty("chapters_count")]
        public long? ChaptersCount { get; set; }

        [JsonProperty("total_count")]
        public long? TotalCount { get; set; }
    }
}
