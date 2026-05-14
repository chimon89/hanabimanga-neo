using System.Collections.Generic;
using Newtonsoft.Json;

namespace hanabimanga.Models
{
    public sealed class ComicReaderNavigationParameter
    {
        public long ComicId { get; set; }
        public long ChapterId { get; set; }
    }

    public sealed class ComicReaderDocument
    {
        public long ComicId { get; set; }
        public string ComicTitle { get; set; } = "";
        public ComicChapter Chapter { get; set; } = new();
        public ComicChapter? PreviousChapter { get; set; }
        public ComicChapter? NextChapter { get; set; }
        public List<ReaderPageImage> Pages { get; set; } = new();
        public string PlatformRouted { get; set; } = "";
        public bool UsesWebFallback => PlatformRouted == "web";
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
