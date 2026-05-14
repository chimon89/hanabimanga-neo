using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace hanabimanga.Models
{
    public sealed class ComicDetail
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public string? Slug { get; set; }
        public string? Summary { get; set; }
        public string? CoverUrl { get; set; }
        public string? PosterUrl { get; set; }
        public List<string> Authors { get; set; } = new();
        public string? Region { get; set; }
        public string? CategoryName { get; set; }
        public string? LockStatus { get; set; }
        public bool IsFinished { get; set; }
        public bool HasUpscaled { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public double RatingAverage { get; set; }
        public int RatingCount { get; set; }
        public long ViewCount { get; set; }
        public string? LatestChapterTitle { get; set; }
        public DateTime? LatestChapterUpdatedAt { get; set; }
        public List<string> Tags { get; set; } = new();
        public List<ComicChapter> Chapters { get; set; } = new();
    }

    public sealed class ComicChapter
    {
        public long ComicId { get; set; }
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public int Index { get; set; }
        public string Category { get; set; } = "normal";
        public string? ChapterFolder { get; set; }
        public int ImageCount { get; set; }
        public string? ImageFormat { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string ReaderKey => $"{ComicId}:{Id}";
    }

    public sealed class ChapterCategoryOption
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public int Count { get; set; }
        public bool IsSelected { get; set; }
    }

    public sealed class ChapterRangeOption
    {
        public int Start { get; set; }
        public int End { get; set; }
        public string Label => $"{Start}-{End}";
        public string Key => $"{Start}:{End}";
        public bool IsSelected { get; set; }
    }

    internal sealed class RawComicRecord
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("slug")]
        public string? Slug { get; set; }

        [JsonProperty("summary")]
        public string? Summary { get; set; }

        [JsonProperty("note")]
        public string? Note { get; set; }

        [JsonProperty("cover_url")]
        public string? CoverUrl { get; set; }

        [JsonProperty("poster_url")]
        public string? PosterUrl { get; set; }

        [JsonProperty("authors")]
        public List<string>? Authors { get; set; }

        [JsonProperty("region")]
        public string? Region { get; set; }

        [JsonProperty("category_id")]
        public long? CategoryId { get; set; }

        [JsonProperty("lock_status")]
        public string? LockStatus { get; set; }

        [JsonProperty("is_finished")]
        public bool IsFinished { get; set; }

        [JsonProperty("has_upscaled")]
        public bool HasUpscaled { get; set; }

        [JsonProperty("release_date")]
        public DateTime? ReleaseDate { get; set; }

        [JsonProperty("rating_average")]
        public double? RatingAverage { get; set; }

        [JsonProperty("rating_count")]
        public int? RatingCount { get; set; }

        [JsonProperty("view_count")]
        public long? ViewCount { get; set; }

        [JsonProperty("latest_chapter_title")]
        public string? LatestChapterTitle { get; set; }

        [JsonProperty("latest_chapter_updated_at")]
        public DateTime? LatestChapterUpdatedAt { get; set; }
    }

    internal sealed class RawChapterRecord
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("comic_id")]
        public long ComicId { get; set; }

        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("idx")]
        public int? Index { get; set; }

        [JsonProperty("category")]
        public string? Category { get; set; }

        [JsonProperty("chapter_folder")]
        public string? ChapterFolder { get; set; }

        [JsonProperty("image_count")]
        public int? ImageCount { get; set; }

        [JsonProperty("image_format")]
        public string? ImageFormat { get; set; }

        [JsonProperty("updated_at")]
        public DateTime? UpdatedAt { get; set; }
    }

    internal sealed class RawCategoryRecord
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }
    }

    internal sealed class RawComicTagRecord
    {
        [JsonProperty("tag")]
        public RawTagRecord? Tag { get; set; }
    }

    internal sealed class RawTagRecord
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }
    }
}
