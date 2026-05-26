using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.Services;

namespace hanabimanga.ViewModels
{
    public sealed class AppSettingsPageViewModel : INotifyPropertyChanged
    {
        private LocalAppSettings _settings = new();
        private LocalStorageStats _stats = new();
        private bool _isLoading;
        private bool _isWorking;
        private bool _isLoaded;
        private string? _errorMessage;
        private string? _feedbackMessage;

        public ObservableCollection<DownloadTaskItem> Downloads { get; } = new();
        public ObservableCollection<ThemeColorOption> AccentColorOptions { get; } = new();

        public bool IsLoaded
        {
            get => _isLoaded;
            private set
            {
                if (_isLoaded == value) return;
                _isLoaded = value;
                OnPropertyChanged();
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading == value) return;
                _isLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBusy));
            }
        }

        public bool IsWorking
        {
            get => _isWorking;
            private set
            {
                if (_isWorking == value) return;
                _isWorking = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBusy));
            }
        }

        public bool IsBusy => IsLoading || IsWorking;

        public string? ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (_errorMessage == value) return;
                _errorMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

        public string? FeedbackMessage
        {
            get => _feedbackMessage;
            private set
            {
                if (_feedbackMessage == value) return;
                _feedbackMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasFeedback));
            }
        }

        public bool HasFeedback => !string.IsNullOrWhiteSpace(FeedbackMessage);

        public bool EnableReaderCache => _settings.EnableReaderCache;
        public bool EnableReaderPreload => _settings.EnableReaderPreload;
        public bool CanUsePreload => EnableReaderCache;
        public double PreloadPageCountValue => _settings.PreloadPageCount;
        public string PreloadPageCountText => $"{_settings.PreloadPageCount} 页";
        public int ReaderViewModeSelectedIndex => _settings.ReaderViewMode == "waterfall" ? 1 : 0;

        public int ApiEndpointSelectedIndex => _settings.ApiEndpoint switch
        {
            "direct" => 1,
            "accelerated" => 2,
            _ => 0,
        };

        public string ActiveEndpointText
        {
            get
            {
                var url = SupabaseService.Instance.CurrentUrl;
                if (string.IsNullOrWhiteSpace(url)) return "未连接";
                if (url == App.AcceleratedUrl) return "当前线路:国内加速 (moedot.net)";
                if (url == App.DirectUrl) return "当前线路:国际线路 (supabase.co)";
                return $"当前线路:{url}";
            }
        }

        public string CacheSummary => $"{_stats.CacheSizeText} · {_stats.CachedFiles} 个文件";
        public string DownloadSummary => $"{_stats.DownloadSizeText} · {_stats.DownloadedChapters} 话";
        public bool HasDownloads => Downloads.Count > 0;
        public bool IsDownloadsEmpty => !HasDownloads;

        public async Task LoadAsync()
        {
            IsLoaded = false;
            IsLoading = true;
            ErrorMessage = null;

            try
            {
                await ReloadCoreAsync();
                IsLoaded = true;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task RefreshAsync()
        {
            IsWorking = true;
            ErrorMessage = null;

            try
            {
                await ReloadCoreAsync();
                FeedbackMessage = "已刷新";
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsWorking = false;
            }
        }

        public async Task SetReaderCacheAsync(bool value)
        {
            if (_settings.EnableReaderCache == value) return;

            _settings.EnableReaderCache = value;
            if (!value)
            {
                _settings.EnableReaderPreload = false;
            }

            await SaveSettingsAsync("设置已保存");
            RaiseSettingsChanged();
        }

        public async Task SetReaderPreloadAsync(bool value)
        {
            if (!_settings.EnableReaderCache) value = false;
            if (_settings.EnableReaderPreload == value) return;

            _settings.EnableReaderPreload = value;
            await SaveSettingsAsync("设置已保存");
            RaiseSettingsChanged();
        }

        public async Task SetPreloadPageCountAsync(int value)
        {
            value = Math.Clamp(value, 1, 8);
            if (_settings.PreloadPageCount == value) return;

            _settings.PreloadPageCount = value;
            await SaveSettingsAsync("设置已保存");
            RaiseSettingsChanged();
        }

        public async Task SetReaderViewModeAsync(int selectedIndex)
        {
            var mode = selectedIndex == 1 ? "waterfall" : "page";
            if (_settings.ReaderViewMode == mode) return;

            _settings.ReaderViewMode = mode;
            await SaveSettingsAsync("设置已保存");
            RaiseSettingsChanged();
        }

        public async Task SetApiEndpointAsync(int selectedIndex)
        {
            var endpoint = selectedIndex switch
            {
                1 => "direct",
                2 => "accelerated",
                _ => "auto",
            };
            if (_settings.ApiEndpoint == endpoint) return;

            _settings.ApiEndpoint = endpoint;

            IsWorking = true;
            ErrorMessage = null;
            try
            {
                await ReaderStorageService.Instance.SaveSettingsAsync(_settings);

                var url = await App.ResolveUrlForEndpointAsync(endpoint);
                await SupabaseService.Instance.SwitchEndpointAsync(url);

                FeedbackMessage = "线路已切换并立即生效";
            }
            catch (Exception ex)
            {
                ErrorMessage = $"线路切换失败:{ex.Message}";
            }
            finally
            {
                IsWorking = false;
            }

            RaiseSettingsChanged();
        }

        public async Task SetAccentColorAsync(string id)
        {
            var resolved = ThemeColorService.Instance.Resolve(id).Id;
            if (_settings.AccentColor == resolved) return;

            _settings.AccentColor = resolved;
            ThemeColorService.Instance.Apply(resolved);
            RefreshAccentColorOptions();
            await SaveSettingsAsync("外观配色已更新");
        }

        public async Task ClearCacheAsync()
        {
            IsWorking = true;
            ErrorMessage = null;

            try
            {
                await ReaderStorageService.Instance.ClearCacheAsync();
                await ReloadStatsAsync();
                FeedbackMessage = "缓存已清理";
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsWorking = false;
            }
        }

        public async Task DeleteDownloadAsync(string id)
        {
            IsWorking = true;
            ErrorMessage = null;

            try
            {
                await ReaderStorageService.Instance.DeleteDownloadAsync(id);
                await ReloadDownloadsAsync();
                await ReloadStatsAsync();
                FeedbackMessage = "下载已删除";
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsWorking = false;
            }
        }

        private async Task SaveSettingsAsync(string feedback)
        {
            IsWorking = true;
            ErrorMessage = null;

            try
            {
                await ReaderStorageService.Instance.SaveSettingsAsync(_settings);
                FeedbackMessage = feedback;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsWorking = false;
            }
        }

        private async Task ReloadCoreAsync()
        {
            _settings = await ReaderStorageService.Instance.LoadSettingsAsync();
            RefreshAccentColorOptions();
            await ReloadStatsAsync();
            await ReloadDownloadsAsync();
            RaiseSettingsChanged();
        }

        private void RefreshAccentColorOptions()
        {
            if (AccentColorOptions.Count == 0)
            {
                foreach (var option in ThemeColorService.Instance.Options)
                {
                    AccentColorOptions.Add(option);
                }
            }

            var selectedId = ThemeColorService.Instance.Resolve(_settings.AccentColor).Id;
            foreach (var option in AccentColorOptions)
            {
                option.IsSelected = option.Id == selectedId;
            }
        }

        private async Task ReloadStatsAsync()
        {
            _stats = await ReaderStorageService.Instance.GetStatsAsync();
            OnPropertyChanged(nameof(CacheSummary));
            OnPropertyChanged(nameof(DownloadSummary));
        }

        private async Task ReloadDownloadsAsync()
        {
            Downloads.Clear();
            foreach (var item in await ReaderStorageService.Instance.GetDownloadsAsync())
            {
                Downloads.Add(item);
            }

            OnPropertyChanged(nameof(HasDownloads));
            OnPropertyChanged(nameof(IsDownloadsEmpty));
        }

        private void RaiseSettingsChanged()
        {
            OnPropertyChanged(nameof(EnableReaderCache));
            OnPropertyChanged(nameof(EnableReaderPreload));
            OnPropertyChanged(nameof(CanUsePreload));
            OnPropertyChanged(nameof(PreloadPageCountValue));
            OnPropertyChanged(nameof(PreloadPageCountText));
            OnPropertyChanged(nameof(ReaderViewModeSelectedIndex));
            OnPropertyChanged(nameof(ApiEndpointSelectedIndex));
            OnPropertyChanged(nameof(ActiveEndpointText));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
