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
    public sealed class LocalStorageService
    {
        private static readonly Lazy<LocalStorageService> _instance =
            new(() => new LocalStorageService());

        public static LocalStorageService Instance => _instance.Value;

        private readonly HttpClient _httpClient = new();
        private readonly SemaphoreSlim _manifestLock = new(1, 1);
        private readonly string _rootPath;
        private readonly string _cachePath;
        private readonly string _downloadsPath;
        private readonly string _settingsPath;
        private readonly string _downloadsManifestPath;
        private LocalAppSettings? _settings;

        private LocalStorageService()
        {
            _rootPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "local-storage");
            _cachePath = Path.Combine(_rootPath, "reader-cache");
            _downloadsPath = Path.Combine(_rootPath, "downloads");
            _settingsPath = Path.Combine(_rootPath, "settings.json");
            _downloadsManifestPath = Path.Combine(_rootPath, "downloads.json");
            Directory.CreateDirectory(_cachePath);
            Directory.CreateDirectory(_downloadsPath);
        }

        public async Task<LocalAppSettings> GetSettingsAsync()
        {
            if (_settings != null) return CloneSettings(_settings);

            Directory.CreateDirectory(_rootPath);
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
            }
            catch
            {
                _settings = new LocalAppSettings();
            }

            _settings.PreloadPageCount = Math.Clamp(_settings.PreloadPageCount, 0, 10);
            return CloneSettings(_settings);
        }

        public async Task SaveSettingsAsync(LocalAppSettings settings)
        {
            settings.PreloadPageCount = Math.Clamp(settings.PreloadPageCount, 0, 10);
            _settings = CloneSettings(settings);
            Directory.CreateDirectory(_rootPath);
            var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
            await File.WriteAllTextAsync(_settingsPath, json);
        }

        public async Task ApplyCachedReaderImagesAsync(ComicReaderDocument document)
        {
            var settings = await GetSettingsAsync();
            if (!settings.EnableReaderCache) return;

            foreach (var page in document.Pages)
            {
                var sourceUrl = GetSourceUrl(page);
                var cachedPath = GetReaderCacheFilePath(document, page, sourceUrl);
                if (!File.Exists(cachedPath)) continue;

                page.LocalCachePath = cachedPath;
                page.Url = ToFileUri(cachedPath);
            }
        }

        public async Task PreloadReaderPagesAsync(ComicReaderDocument document, int currentPage)
        {
            var settings = await GetSettingsAsync();
            if (!settings.EnableReaderCache || !settings.EnableReaderPreload || settings.PreloadPageCount <= 0)
            {
                return;
            }

            var start = Math.Max(currentPage, 1);
            var end = Math.Min(document.Pages.Count, start + settings.PreloadPageCount);
            for (var pageNumber = start; pageNumber <= end; pageNumber++)
            {
                var page = document.Pages.ElementAtOrDefault(pageNumber - 1);
                if (page == null) continue;

                await CacheReaderPageAsync(document, page);
            }
        }

        public async Task CacheReaderPageAsync(ComicReaderDocument document, ReaderPageImage page)
        {
            var settings = await GetSettingsAsync();
            if (!settings.EnableReaderCache) return;

            var sourceUrl = GetSourceUrl(page);
            if (string.IsNullOrWhiteSpace(sourceUrl)) return;

            var targetPath = GetReaderCacheFilePath(document, page, sourceUrl);
            if (!File.Exists(targetPath))
            {
                await DownloadToFileAsync(sourceUrl, targetPath);
            }

            page.LocalCachePath = targetPath;
            page.Url = ToFileUri(targetPath);
            page.LoadFailed = false;
        }

        public async Task<DownloadTaskItem> DownloadReaderChapterAsync(
            ComicReaderDocument document,
            IProgress<DownloadTaskItem>? progress = null)
        {
            var item = new DownloadTaskItem
            {
                Id = BuildDownloadId(document),
                ComicId = document.ComicId,
                ChapterId = document.Chapter.Id,
                ComicTitle = document.ComicTitle,
                ChapterTitle = document.Chapter.Title,
                IsUpscaled = document.IsUpscaled,
                TotalPages = document.Pages.Count,
                Status = "downloading",
                FolderPath = GetDownloadFolderPath(document),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            await UpsertDownloadAsync(item);
            progress?.Report(item);

            try
            {
                Directory.CreateDirectory(item.FolderPath!);
                foreach (var page in document.Pages.OrderBy(page => page.PageNumber))
                {
                    var sourceUrl = GetSourceUrl(page);
                    if (string.IsNullOrWhiteSpace(sourceUrl)) continue;

                    var targetPath = Path.Combine(
                        item.FolderPath!,
                        $"{page.PageLabel}{GetExtensionFromUrl(sourceUrl)}");

                    if (!File.Exists(targetPath))
                    {
                        var cachedPath = GetReaderCacheFilePath(document, page, sourceUrl);
                        if (File.Exists(cachedPath))
                        {
                            File.Copy(cachedPath, targetPath, overwrite: true);
                        }
                        else
                        {
                            await DownloadToFileAsync(sourceUrl, targetPath);
                        }
                    }

                    item.DownloadedPages++;
                    item.SizeBytes += new FileInfo(targetPath).Length;
                    item.Status = "downloading";
                    item.Touch();
                    await UpsertDownloadAsync(item);
                    progress?.Report(item);
                }

                item.Status = "completed";
                item.Touch();
                await UpsertDownloadAsync(item);
                progress?.Report(item);
                return item;
            }
            catch (Exception ex)
            {
                item.Status = "failed";
                item.ErrorMessage = ex.Message;
                item.Touch();
                await UpsertDownloadAsync(item);
                progress?.Report(item);
                return item;
            }
        }

        public async Task<List<DownloadTaskItem>> GetDownloadsAsync()
        {
            await _manifestLock.WaitAsync();
            try
            {
                return await ReadDownloadsUnsafeAsync();
            }
            finally
            {
                _manifestLock.Release();
            }
        }

        public async Task DeleteDownloadAsync(string downloadId)
        {
            if (string.IsNullOrWhiteSpace(downloadId)) return;

            await _manifestLock.WaitAsync();
            try
            {
                var downloads = await ReadDownloadsUnsafeAsync();
                var item = downloads.FirstOrDefault(download => download.Id == downloadId);
                if (item?.FolderPath is { } folder && Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }

                downloads.RemoveAll(download => download.Id == downloadId);
                await WriteDownloadsUnsafeAsync(downloads);
            }
            finally
            {
                _manifestLock.Release();
            }
        }

        public async Task<LocalStorageStats> GetStatsAsync()
        {
            var downloads = await GetDownloadsAsync();
            return new LocalStorageStats
            {
                CacheBytes = GetDirectorySize(_cachePath),
                CachedFiles = Directory.Exists(_cachePath)
                    ? Directory.EnumerateFiles(_cachePath, "*", SearchOption.AllDirectories).Count()
                    : 0,
                DownloadBytes = GetDirectorySize(_downloadsPath),
                DownloadedChapters = downloads.Count(download => download.Status == "completed"),
            };
        }

        public async Task ClearReaderCacheAsync()
        {
            if (Directory.Exists(_cachePath))
            {
                Directory.Delete(_cachePath, recursive: true);
            }

            Directory.CreateDirectory(_cachePath);
            await Task.CompletedTask;
        }

        private async Task UpsertDownloadAsync(DownloadTaskItem item)
        {
            await _manifestLock.WaitAsync();
            try
            {
                var downloads = await ReadDownloadsUnsafeAsync();
                downloads.RemoveAll(download => download.Id == item.Id);
                downloads.Add(item);
                await WriteDownloadsUnsafeAsync(downloads);
            }
            finally
            {
                _manifestLock.Release();
            }
        }

        private async Task<List<DownloadTaskItem>> ReadDownloadsUnsafeAsync()
        {
            Directory.CreateDirectory(_rootPath);
            if (!File.Exists(_downloadsManifestPath)) return new List<DownloadTaskItem>();

            try
            {
                var json = await File.ReadAllTextAsync(_downloadsManifestPath);
                return JsonConvert.DeserializeObject<List<DownloadTaskItem>>(json)
                    ?.OrderByDescending(item => item.UpdatedAt)
                    .ToList() ?? new List<DownloadTaskItem>();
            }
            catch
            {
                return new List<DownloadTaskItem>();
            }
        }

        private async Task WriteDownloadsUnsafeAsync(List<DownloadTaskItem> downloads)
        {
            Directory.CreateDirectory(_rootPath);
            var json = JsonConvert.SerializeObject(
                downloads.OrderByDescending(item => item.UpdatedAt).ToList(),
                Formatting.Indented);
            await File.WriteAllTextAsync(_downloadsManifestPath, json);
        }

        private async Task DownloadToFileAsync(string sourceUrl, string targetPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            var tempPath = $"{targetPath}.tmp";
            using var response = await _httpClient.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var sourceStream = await response.Content.ReadAsStreamAsync())
            await using (var fileStream = File.Create(tempPath))
            {
                await sourceStream.CopyToAsync(fileStream);
            }

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            File.Move(tempPath, targetPath);
        }

        private string GetReaderCacheFilePath(
            ComicReaderDocument document,
            ReaderPageImage page,
            string sourceUrl)
        {
            return Path.Combine(
                _cachePath,
                document.ComicId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                document.Chapter.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                document.IsUpscaled ? "vip" : "sd",
                $"{page.PageLabel}{GetExtensionFromUrl(sourceUrl)}");
        }

        private string GetDownloadFolderPath(ComicReaderDocument document)
        {
            return Path.Combine(
                _downloadsPath,
                document.ComicId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                document.Chapter.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                document.IsUpscaled ? "vip" : "sd");
        }

        private static string BuildDownloadId(ComicReaderDocument document)
            => $"{document.ComicId}:{document.Chapter.Id}:{(document.IsUpscaled ? "vip" : "sd")}";

        private static string GetSourceUrl(ReaderPageImage page)
            => string.IsNullOrWhiteSpace(page.OriginalUrl) ? page.Url : page.OriginalUrl;

        private static string GetExtensionFromUrl(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var extension = Path.GetExtension(uri.AbsolutePath);
                if (IsImageExtension(extension)) return extension.ToLowerInvariant();
            }

            return ".webp";
        }

        private static bool IsImageExtension(string extension)
            => extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);

        private static long GetDirectorySize(string path)
        {
            if (!Directory.Exists(path)) return 0;

            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Select(file =>
                {
                    try
                    {
                        return new FileInfo(file).Length;
                    }
                    catch
                    {
                        return 0L;
                    }
                })
                .Sum();
        }

        private static string ToFileUri(string path)
            => new Uri(path).AbsoluteUri;

        private static LocalAppSettings CloneSettings(LocalAppSettings settings) => new()
        {
            EnableReaderCache = settings.EnableReaderCache,
            EnableReaderPreload = settings.EnableReaderPreload,
            PreloadPageCount = settings.PreloadPageCount,
        };
    }
}
