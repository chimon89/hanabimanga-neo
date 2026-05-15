using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace hanabimanga.Models
{
    public sealed class BookshelfDocument
    {
        public List<BookshelfComicItem> Favorites { get; set; } = new();
        public List<BookshelfComicItem> Likes { get; set; } = new();
    }

    public sealed class BookshelfComicItem
    {
        public string Id { get; set; } = "";
        public long ComicId { get; set; }
        public string Title { get; set; } = "";
        public string? Subtitle { get; set; }
        public string? CoverUrl { get; set; }
        public DateTime? AddedAt { get; set; }
    }

    public sealed class ComicInteractionState
    {
        public bool IsFavorite { get; set; }
        public bool IsLiked { get; set; }
    }

    internal sealed class RawComicInteractionRecord
    {
        [JsonProperty("comic_id")]
        public long ComicId { get; set; }

        [JsonProperty("created_at")]
        public DateTime? CreatedAt { get; set; }
    }
}
