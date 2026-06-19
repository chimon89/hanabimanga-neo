using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using hanabimanga.Models;
using Newtonsoft.Json;
using Windows.Storage;

namespace hanabimanga.Services
{
    public sealed class ReaderStorageService
    {
        private const string SettingsFileName = "app-settings.json";
        private const string DownloadsFileName = "downloads.json";
        private static readonly string[] KnownImageExtensions = [".webp", ".jpg", ".jpeg", ".png"];

        private readonly HttpClient _httpClient = new();
        private readonly SemaphoreSlim _manifestLock = new(1, 1);
        private readonly string _localRoot;
        private readonly string _cacheRoot;
        private readonly string _downloadRoot;
        private readonly string _settingsPath;
        private readonly string _downloadsPath;

        private LocalAppSettings? _settings;

        public static ReaderStorageService Instance { get; } = new();

        private ReaderStorageService()
        {
            _localRoot = ApplicationData.Current.LocalFolder.Path;
            _cacheRoot = Path.Combine(_localRoot, "reader-cache");
            _downloadRoot = Path.Combine(_localRoot, "reader-downloads");
            _settingsPath = Path.Combine(_localRoot, SettingsFileName);
            _downloadsPath = Path.Combine(_localRoot, DownloadsFileName);

            Directory.CreateDirectory(_cacheRoot);
            Directory.CreateDirectory(_downloadRoot);
        }

        public async Task<LocalAppSettings> LoadSettingsAsync()
        {
            if (_settings != null) return CloneSettings(_settings);

            if (!File.Exists(_settingsPath))
            {
                _settings = new LocalAppSettings();
                await SaveSettingsAsync(_settings);
                return CloneSettings(_settings);
            }

            try
            {
                var json = await File.ReadAllTextAsync(_settingsPath);
                _settings = JsonConvert.DeserializeObject<LocalAppSettings>(json) ?? new LocalAppSettings();
                NormalizeSettings(_settings);
            }
            catch
            {
                _settings = new LocalAppSettings();
            }

            return CloneSettings(_settings);
        }

        public async Task SaveSettingsAsync(LocalAppSettings settings)
        {
            NormalizeSettings(settings);
            _settings = CloneSettings(settings);
            Directory.CreateDirectory(_localRoot);
            var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
            await File.WriteAllTextAsync(_settingsPath, json);
        }

        public async Task ApplyCachedPagesAsync(ComicReaderDocument document)
        {
            var settings = await LoadSettingsAsync();
            if (!settings.EnableReaderCache) return;

            foreach (var page in document.Pages)
            {
                if (string.IsNullOrWhiteSpace(page.OriginalUrl))
                {
                    page.OriginalUrl = page.Url;
                }

                var path = GetCacheFilePath(document, page);
                if (!File.Exists(path)) continue;

                page.LocalCachePath = path;
                page.Url = ToFileUri(path);
                page.LoadFailed = false;
            }
        }

        public async Task PreloadAsync(ComicReaderDocument document, int currentPage, CancellationToken cancellationToken = default)
        {
            var settings = await LoadSettingsAsync();
            if (!settings.EnableReaderCache || !settings.EnableReaderPreload) return;
            if (document.Pages.Count == 0) return;

            var startIndex = Math.Clamp(currentPage, 1, document.Pages.Count) - 1;
            var endIndex = Math.Min(document.Pages.Count - 1, startIndex + settings.PreloadPageCount);

            for (var index = startIndex; index <= endIndex; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = document.Pages[index];
                try
                {
                    var path = await EnsureCachedPageAsync(document, page, cancellationToken);
                    if (File.Exists(path))
                    {
                        page.LocalCachePath = path;
                        page.Url = ToFileUri(path);
                        page.LoadFailed = false;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[reader-cache] preload page {page.PageLabel} failed: {ex.Message}");
                }
            }
        }

        public async Task<DownloadTaskItem> DownloadChapterAsync(
            ComicReaderDocument document,
            Action<DownloadTaskItem>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var task = CreateDownloadTask(document);
            Directory.CreateDirectory(task.FolderPath!);

            await UpsertDownloadAsync(task);
            progress?.Invoke(task);

            try
            {
                task.Status = "downloading";
                task.Touch();
                await UpsertDownloadAsync(task);
                progress?.Invoke(task);

                foreach (var page in document.Pages.OrderBy(page => page.PageNumber))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var cachePath = await EnsureCachedPageAsync(document, page, cancellationToken);
                    var downloadPath = GetDownloadFilePath(task.FolderPath!, page);
                    Directory.CreateDirectory(Path.GetDirectoryName(downloadPath)!);

                    if (!File.Exists(downloadPath))
                    {
                        File.Copy(cachePath, downloadPath, overwrite: true);
                    }

                    page.LocalCachePath = cachePath;
                    page.Url = ToFileUri(cachePath);
                    page.LoadFailed = false;

                    task.DownloadedPages = Math.Min(task.DownloadedPages + 1, task.TotalPages);
                    task.SizeBytes = GetDirectorySize(task.FolderPath!);
                    task.Touch();
                    await UpsertDownloadAsync(task);
                    progress?.Invoke(task);
                }

                task.Status = "completed";
                task.DownloadedPages = task.TotalPages;
                task.SizeBytes = GetDirectorySize(task.FolderPath!);
                task.ErrorMessage = null;
                task.Touch();
                await UpsertDownloadAsync(task);
                progress?.Invoke(task);
                return task;
            }
            catch (Exception ex)
            {
                task.Status = "failed";
                task.ErrorMessage = ex.Message;
                task.SizeBytes = Directory.Exists(task.FolderPath)
                    ? GetDirectorySize(task.FolderPath!)
                    : 0;
                task.Touch();
                await UpsertDownloadAsync(task);
                progress?.Invoke(task);
                throw;
            }
        }

        public async Task<IReadOnlyList<DownloadTaskItem>> GetDownloadsAsync()
        {
            await _manifestLock.WaitAsync();
            try
            {
                return await ReadDownloadsUnlockedAsync();
            }
            finally
            {
                _manifestLock.Release();
            }
        }

        public async Task DeleteDownloadAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;

            await _manifestLock.WaitAsync();
            try
            {
                var downloads = await ReadDownloadsUnlockedAsync();
                var item = downloads.FirstOrDefault(download => download.Id == id);
                if (item?.FolderPath is { Length: > 0 } folderPath &&
                    Directory.Exists(folderPath) &&
                    IsUnderRoot(folderPath, _downloadRoot))
                {
                    Directory.Delete(folderPath, recursive: true);
                }

                downloads.RemoveAll(download => download.Id == id);
                await WriteDownloadsUnlockedAsync(downloads);
            }
            finally
            {
                _manifestLock.Release();
            }
        }

        public async Task ClearCacheAsync()
        {
            if (Directory.Exists(_cacheRoot))
            {
                Directory.Delete(_cacheRoot, recursive: true);
            }

            Directory.CreateDirectory(_cacheRoot);
            await Task.CompletedTask;
        }

        public async Task<LocalStorageStats> GetStatsAsync()
        {
            var downloads = await GetDownloadsAsync();
            var cache = GetDirectoryStats(_cacheRoot);
            var download = GetDirectoryStats(_downloadRoot);

            return new LocalStorageStats
            {
                CacheBytes = cache.Bytes,
                CachedFiles = cache.Files,
                DownloadBytes = download.Bytes,
                DownloadedChapters = downloads.Count(item => item.Status == "completed"),
            };
        }

        private async Task<string> EnsureCachedPageAsync(
            ComicReaderDocument document,
            ReaderPageImage page,
            CancellationToken cancellationToken)
        {
            var path = GetCacheFilePath(document, page);
            if (File.Exists(path)) return path;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var sourceUrl = string.IsNullOrWhiteSpace(page.OriginalUrl)
                ? page.Url
                : page.OriginalUrl;

            if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri) && sourceUri.IsFile)
            {
                File.Copy(sourceUri.LocalPath, path, overwrite: true);
                return path;
            }

            await DownloadFileAsync(sourceUrl, path, cancellationToken);
            return path;
        }

        private async Task DownloadFileAsync(string url, string targetPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new InvalidOperationException("图片地址为空。");
            }

            var tempPath = targetPath + ".tmp";
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(tempPath))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            File.Move(tempPath, targetPath);
        }

        private async Task UpsertDownloadAsync(DownloadTaskItem item)
        {
            await _manifestLock.WaitAsync();
            try
            {
                var downloads = await ReadDownloadsUnlockedAsync();
                downloads.RemoveAll(download => download.Id == item.Id);
                downloads.Insert(0, CloneDownload(item));
                await WriteDownloadsUnlockedAsync(downloads);
            }
            finally
            {
                _manifestLock.Release();
            }
        }

        private async Task<List<DownloadTaskItem>> ReadDownloadsUnlockedAsync()
        {
            if (!File.Exists(_downloadsPath)) return [];

            try
            {
                var json = await File.ReadAllTextAsync(_downloadsPath);
                return JsonConvert.DeserializeObject<List<DownloadTaskItem>>(json) ?? [];
            }
            catch
            {
                return [];
            }
        }

        private async Task WriteDownloadsUnlockedAsync(List<DownloadTaskItem> downloads)
        {
            Directory.CreateDirectory(_localRoot);
            var json = JsonConvert.SerializeObject(downloads, Formatting.Indented);
            await File.WriteAllTextAsync(_downloadsPath, json);
        }

        private DownloadTaskItem CreateDownloadTask(ComicReaderDocument document)
        {
            var qualityKey = document.IsUpscaled ? "vip" : "sd";
            var id = $"{document.ComicId}-{document.Chapter.Id}-{qualityKey}";
            var folder = Path.Combine(_downloadRoot, id);

            return new DownloadTaskItem
            {
                Id = id,
                ComicId = document.ComicId,
                ChapterId = document.Chapter.Id,
                ComicTitle = document.ComicTitle,
                ChapterTitle = document.Chapter.Title,
                IsUpscaled = document.IsUpscaled,
                TotalPages = document.Pages.Count,
                DownloadedPages = 0,
                SizeBytes = Directory.Exists(folder) ? GetDirectorySize(folder) : 0,
                Status = "queued",
                FolderPath = folder,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
        }

        private string GetCacheFilePath(ComicReaderDocument document, ReaderPageImage page)
        {
            var qualityKey = document.IsUpscaled ? "vip" : "sd";
            var extension = GetImageExtension(page.OriginalUrl);
            return Path.Combine(
                _cacheRoot,
                document.ComicId.ToString(),
                document.Chapter.Id.ToString(),
                qualityKey,
                $"{page.PageLabel}-{HashText(page.OriginalUrl)}{extension}");
        }

        private static string GetDownloadFilePath(string folderPath, ReaderPageImage page)
        {
            var extension = GetImageExtension(page.OriginalUrl);
            return Path.Combine(folderPath, $"{page.PageLabel}{extension}");
        }

        private static string GetImageExtension(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url) &&
                Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
                if (KnownImageExtensions.Contains(extension)) return extension;
            }

            return ".webp";
        }

        private static string HashText(string? text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
            return Convert.ToHexString(bytes)[..10].ToLowerInvariant();
        }

        private static (long Bytes, int Files) GetDirectoryStats(string folder)
        {
            if (!Directory.Exists(folder)) return (0, 0);

            try
            {
                long bytes = 0;
                var files = 0;
                foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    files++;
                    bytes += new FileInfo(path).Length;
                }

                return (bytes, files);
            }
            catch
            {
                return (0, 0);
            }
        }

        private static long GetDirectorySize(string folder)
        {
            if (!Directory.Exists(folder)) return 0;
            try
            {
                return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length);
            }
            catch
            {
                return 0;
            }
        }

        private static bool IsUnderRoot(string path, string root)
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string ToFileUri(string path) => new Uri(path).AbsoluteUri;

        private static void NormalizeSettings(LocalAppSettings settings)
        {
            settings.PreloadPageCount = Math.Clamp(settings.PreloadPageCount, 6, 10);
            settings.ReaderViewMode = settings.ReaderViewMode?.Trim().ToLowerInvariant() == "waterfall"
                ? "waterfall"
                : "page";
            settings.ApiEndpoint = settings.ApiEndpoint?.Trim().ToLowerInvariant() switch
            {
                "direct" => "direct",
                "accelerated" => "accelerated",
                _ => "auto",
            };
            settings.AccentColor = ThemeColorService.Instance.Resolve(settings.AccentColor?.Trim()).Id;
        }

        private static LocalAppSettings CloneSettings(LocalAppSettings settings) => new()
        {
            EnableReaderCache = settings.EnableReaderCache,
            EnableReaderPreload = settings.EnableReaderPreload,
            PreloadPageCount = settings.PreloadPageCount,
            ReaderViewMode = settings.ReaderViewMode,
            HasSeenReaderZoomGuide = settings.HasSeenReaderZoomGuide,
            ApiEndpoint = settings.ApiEndpoint,
            AccentColor = settings.AccentColor,
        };

        private static DownloadTaskItem CloneDownload(DownloadTaskItem item) => new()
        {
            Id = item.Id,
            ComicId = item.ComicId,
            ChapterId = item.ChapterId,
            ComicTitle = item.ComicTitle,
            ChapterTitle = item.ChapterTitle,
            IsUpscaled = item.IsUpscaled,
            TotalPages = item.TotalPages,
            DownloadedPages = item.DownloadedPages,
            SizeBytes = item.SizeBytes,
            Status = item.Status,
            ErrorMessage = item.ErrorMessage,
            FolderPath = item.FolderPath,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
        };
    }
}
