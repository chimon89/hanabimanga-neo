using System;
using Newtonsoft.Json;

namespace hanabimanga.Models
{
    public sealed class RecentReadingProgress
    {
        public long ComicId { get; set; }
        public long ChapterId { get; set; }
        public int PageIndex { get; set; }
        public int TotalPages { get; set; }
        public string ComicTitle { get; set; } = "";
        public string ChapterTitle { get; set; } = "";
        public string? CoverUrl { get; set; }
        public DateTime? LastReadAt { get; set; }
        public string ProgressText =>
            TotalPages > 0
                ? $"第 {Math.Clamp(PageIndex, 1, TotalPages)}/{TotalPages} 页"
                : "继续阅读";
    }

    internal sealed class RawReadingHistoryRecord
    {
        [JsonProperty("user_id")]
        public string? UserId { get; set; }

        [JsonProperty("comic_id")]
        public long ComicId { get; set; }

        [JsonProperty("chapter_id")]
        public long? ChapterId { get; set; }

        [JsonProperty("page_index")]
        public int? PageIndex { get; set; }

        [JsonProperty("chapter_title")]
        public string? ChapterTitle { get; set; }

        [JsonProperty("chapter_index")]
        public int? ChapterIndex { get; set; }

        [JsonProperty("total_pages")]
        public int? TotalPages { get; set; }

        [JsonProperty("total_chapters")]
        public int? TotalChapters { get; set; }

        [JsonProperty("last_read_at")]
        public DateTime? LastReadAt { get; set; }
    }
}
