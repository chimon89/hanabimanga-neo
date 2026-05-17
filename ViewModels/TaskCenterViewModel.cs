using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.Services;

namespace hanabimanga.ViewModels
{
    public sealed class TaskCenterViewModel : INotifyPropertyChanged
    {
        private TaskCenterDocument _document = new();
        private List<PointTransaction> _allTransactions = new();
        private string _transactionFilter = "all";
        private bool _isLoading;
        private bool _isWorking;
        private string? _errorMessage;
        private string? _feedbackMessage;

        public ObservableCollection<TaskCenterSignInDay> SignInDays { get; } = new();
        public ObservableCollection<TaskCenterTaskItem> DailyTasks { get; } = new();
        public ObservableCollection<TaskCenterTaskItem> OneTimeTasks { get; } = new();
        public ObservableCollection<TaskCenterTaskItem> LongTermTasks { get; } = new();
        public ObservableCollection<PointTransaction> Transactions { get; } = new();
        public ObservableCollection<PointStoreItem> VirtualStoreItems { get; } = new();
        public ObservableCollection<PointStoreItem> PhysicalStoreItems { get; } = new();
        public ObservableCollection<ExchangeRecord> ExchangeRecords { get; } = new();

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

        public int Points => _document.Points;
        public string TodayPointsText => _document.TodayPointsText;
        public string EarnedPointsText => _document.EarnedPointsText;
        public string SpentPointsText => _document.SpentPointsText;
        public int SignInStreak => _document.SignInStreak;
        public string SignInStreakText => $"已连续 {SignInStreak} 天";
        public bool HasSignedInToday => _document.HasSignedInToday;
        public string SignInButtonText => HasSignedInToday ? "已签到 · 明日见" : "签到领积分";
        public string DailyTaskSummary => $"{_document.DailyCompletedCount}/{_document.DailyTotalCount}";
        public string OneTimeTaskSummary => $"{_document.OneTimeCompletedCount}/{_document.OneTimeTotalCount}";
        public string LongTermTaskSummary => $"{_document.LongTermCompletedCount}/{_document.LongTermTotalCount}";
        public bool HasTransactions => Transactions.Count > 0;
        public bool IsTransactionsEmpty => !HasTransactions;
        public bool HasExchangeRecords => ExchangeRecords.Count > 0;
        public bool IsExchangeRecordsEmpty => !HasExchangeRecords;

        public async Task LoadAsync()
        {
            IsLoading = true;
            ErrorMessage = null;

            try
            {
                UpdateFromDocument(await TaskCenterService.Instance.GetTaskCenterAsync());
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
                UpdateFromDocument(await TaskCenterService.Instance.GetTaskCenterAsync());
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

        public async Task ClaimSignInAsync()
        {
            if (HasSignedInToday) return;

            IsWorking = true;
            ErrorMessage = null;

            try
            {
                UpdateFromDocument(await TaskCenterService.Instance.ClaimDailySignInAsync());
                FeedbackMessage = "签到成功";
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

        public async Task RedeemAsync(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId)) return;

            IsWorking = true;
            ErrorMessage = null;

            try
            {
                UpdateFromDocument(await TaskCenterService.Instance.RedeemAsync(itemId));
                FeedbackMessage = "兑换已提交";
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

        public void SetTransactionFilter(string filter)
        {
            _transactionFilter = string.IsNullOrWhiteSpace(filter) ? "all" : filter;
            RefreshTransactions();
        }

        private void UpdateFromDocument(TaskCenterDocument document)
        {
            _document = document;
            _allTransactions = document.Transactions.ToList();

            Replace(SignInDays, document.SignInDays);
            Replace(DailyTasks, document.DailyTasks);
            Replace(OneTimeTasks, document.OneTimeTasks);
            Replace(LongTermTasks, document.LongTermTasks);
            Replace(VirtualStoreItems, document.StoreItems.Where(item => item.IsVirtual));
            Replace(PhysicalStoreItems, document.StoreItems.Where(item => item.IsPhysical));
            Replace(ExchangeRecords, document.ExchangeRecords);
            RefreshTransactions();

            RaiseDocumentProperties();
        }

        private void RefreshTransactions()
        {
            var items = _transactionFilter switch
            {
                "income" => _allTransactions.Where(item => item.Amount >= 0),
                "expense" => _allTransactions.Where(item => item.Amount < 0),
                _ => _allTransactions,
            };

            Replace(Transactions, items);
            OnPropertyChanged(nameof(HasTransactions));
            OnPropertyChanged(nameof(IsTransactionsEmpty));
        }

        private void RaiseDocumentProperties()
        {
            OnPropertyChanged(nameof(Points));
            OnPropertyChanged(nameof(TodayPointsText));
            OnPropertyChanged(nameof(EarnedPointsText));
            OnPropertyChanged(nameof(SpentPointsText));
            OnPropertyChanged(nameof(SignInStreak));
            OnPropertyChanged(nameof(SignInStreakText));
            OnPropertyChanged(nameof(HasSignedInToday));
            OnPropertyChanged(nameof(SignInButtonText));
            OnPropertyChanged(nameof(DailyTaskSummary));
            OnPropertyChanged(nameof(OneTimeTaskSummary));
            OnPropertyChanged(nameof(LongTermTaskSummary));
            OnPropertyChanged(nameof(HasExchangeRecords));
            OnPropertyChanged(nameof(IsExchangeRecordsEmpty));
        }

        private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
        {
            collection.Clear();
            foreach (var item in items)
            {
                collection.Add(item);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
