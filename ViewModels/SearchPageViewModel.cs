using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.Services;

namespace hanabimanga.ViewModels
{
    public sealed class SearchPageViewModel : INotifyPropertyChanged
    {
        private bool _isLoading;
        private string? _errorMessage;
        private string _query = "";
        private long _totalCount;

        public ObservableCollection<ComicListItem> Results { get; } = new();

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
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Summary));
            }
        }

        public long TotalCount
        {
            get => _totalCount;
            private set
            {
                if (_totalCount == value) return;
                _totalCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Summary));
            }
        }

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public bool HasResults => Results.Count > 0;
        public bool IsEmpty => !IsLoading && !HasResults && !HasError;
        public string Title => string.IsNullOrWhiteSpace(Query) ? "搜索" : $"搜索: {Query}";
        public string Summary => HasResults ? $"找到 {TotalCount} 个结果" : "输入关键词查找漫画、别名或拼音";

        public async Task SearchAsync(string? query)
        {
            if (!SupabaseService.Instance.IsInitialized)
            {
                ErrorMessage = "Supabase 未初始化:请检查 appsettings.local.json 中的 Url / AnonKey。";
                return;
            }

            IsLoading = true;
            ErrorMessage = null;
            Query = query?.Trim() ?? "";
            Results.Clear();
            RefreshResultProperties();

            try
            {
                var document = await SupabaseService.Instance.SearchComicsAsync(Query);
                TotalCount = document.TotalCount;
                foreach (var item in document.Items)
                    Results.Add(item);
                RefreshResultProperties();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"搜索失败:{ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void RefreshResultProperties()
        {
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(Summary));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
