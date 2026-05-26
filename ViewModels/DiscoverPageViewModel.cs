using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.Services;

namespace hanabimanga.ViewModels
{
    public sealed class DiscoverPageViewModel : INotifyPropertyChanged
    {
        private bool _isLoading;
        private string? _errorMessage;
        private string _query = "";
        private string _sectionTitle = "随机推荐";
        private string _sectionSummary = "随便看看，遇到一本正好想读的";
        private bool _isRandomMode = true;

        public ObservableCollection<ComicListItem> Items { get; } = new();

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading == value) return;
                _isLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEmpty));
            }
        }

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

        public string Query
        {
            get => _query;
            private set
            {
                if (_query == value) return;
                _query = value;
                OnPropertyChanged();
            }
        }

        public string SectionTitle
        {
            get => _sectionTitle;
            private set
            {
                if (_sectionTitle == value) return;
                _sectionTitle = value;
                OnPropertyChanged();
            }
        }

        public string SectionSummary
        {
            get => _sectionSummary;
            private set
            {
                if (_sectionSummary == value) return;
                _sectionSummary = value;
                OnPropertyChanged();
            }
        }

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public bool HasItems => Items.Count > 0;
        public bool IsEmpty => !IsLoading && !HasItems && !HasError;
        public bool ShowItemSubtitle => !IsRandomMode;

        private bool IsRandomMode
        {
            get => _isRandomMode;
            set
            {
                if (_isRandomMode == value) return;
                _isRandomMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowItemSubtitle));
            }
        }

        public async Task LoadRandomAsync()
        {
            await LoadAsync(
                async () => await SupabaseService.Instance.GetRandomComicsAsync(18),
                title: "随机推荐",
                summary: "随便看看，遇到一本正好想读的",
                query: "",
                isRandomMode: true);
        }

        public async Task SearchAsync(string? query)
        {
            var normalized = query?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(normalized))
            {
                await LoadRandomAsync();
                return;
            }

            await LoadAsync(
                async () => (await SupabaseService.Instance.SearchComicsAsync(normalized)).Items,
                title: $"搜索: {normalized}",
                summary: "按标题、别名和拼音查找漫画",
                query: normalized,
                isRandomMode: false);
        }

        private async Task LoadAsync(
            Func<Task<System.Collections.Generic.List<ComicListItem>>> loader,
            string title,
            string summary,
            string query,
            bool isRandomMode)
        {
            IsLoading = true;
            ErrorMessage = null;
            Query = query;
            SectionTitle = title;
            SectionSummary = summary;
            IsRandomMode = isRandomMode;
            Items.Clear();
            RefreshItemProperties();
            await App.SupabaseInitialization;

            if (!SupabaseService.Instance.IsInitialized)
            {
                ErrorMessage = "Supabase 未初始化:请检查 appsettings.local.json 中的 Url / AnonKey。";
                IsLoading = false;
                return;
            }

            try
            {
                var items = await loader();
                foreach (var item in items)
                    Items.Add(item);
                RefreshItemProperties();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"发现页加载失败:{ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void RefreshItemProperties()
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
