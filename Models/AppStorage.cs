using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace hanabimanga.Models
{
    public sealed class LocalAppSettings
    {
        public bool EnableReaderCache { get; set; } = true;
        public bool EnableReaderPreload { get; set; } = true;
        public int PreloadPageCount { get; set; } = 3;
        public string ReaderViewMode { get; set; } = "page";
        public bool HasSeenReaderZoomGuide { get; set; }

        /// <summary>
        /// 接口线路偏好:"auto"(自动检测)、"direct"(国际线路)、"accelerated"(国内加速)。
        /// </summary>
        public string ApiEndpoint { get; set; } = "auto";

        /// <summary>
        /// 外观配色 id,取值见 ThemeColorService.Options。
        /// </summary>
        public string AccentColor { get; set; } = "sakura";
    }

    public sealed class LocalStorageStats
    {
        public long CacheBytes { get; set; }
        public int CachedFiles { get; set; }
        public long DownloadBytes { get; set; }
        public int DownloadedChapters { get; set; }

        public string CacheSizeText => FormatBytes(CacheBytes);
        public string DownloadSizeText => FormatBytes(DownloadBytes);

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";

            var value = bytes / 1024.0;
            if (value < 1024) return $"{value:0.0} KB";

            value /= 1024.0;
            if (value < 1024) return $"{value:0.0} MB";

            value /= 1024.0;
            return $"{value:0.0} GB";
        }
    }

    public sealed class DownloadTaskItem : INotifyPropertyChanged
    {
        public string Id { get; set; } = "";
        public long ComicId { get; set; }
        public long ChapterId { get; set; }
        public string ComicTitle { get; set; } = "";
        public string ChapterTitle { get; set; } = "";
        public bool IsUpscaled { get; set; }
        public int TotalPages { get; set; }
        public int DownloadedPages { get; set; }
        public long SizeBytes { get; set; }
        public string Status { get; set; } = "queued";
        public string? ErrorMessage { get; set; }
        public string? FolderPath { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [JsonIgnore]
        public string Title => string.IsNullOrWhiteSpace(ChapterTitle)
            ? ComicTitle
            : $"{ComicTitle} · {ChapterTitle}";

        [JsonIgnore]
        public string QualityText => IsUpscaled ? "AI 超分" : "标清";

        [JsonIgnore]
        public string ProgressText => TotalPages > 0
            ? $"{DownloadedPages}/{TotalPages} 页"
            : "等待下载";

        [JsonIgnore]
        public string SizeText => LocalStorageStats.FormatBytes(SizeBytes);

        [JsonIgnore]
        public string StatusText => Status switch
        {
            "completed" => "已下载",
            "downloading" => "下载中",
            "failed" => "下载失败",
            "deleted" => "已删除",
            _ => "等待中",
        };

        [JsonIgnore]
        public bool IsActive => Status == "downloading" || Status == "queued";

        [JsonIgnore]
        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

        public void Touch()
        {
            UpdatedAt = DateTime.UtcNow;
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(HasError));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
