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
        private PaymentOrder? _latestPaymentOrder;

        public ObservableCollection<TaskCenterSignInDay> SignInDays { get; } = new();
        public ObservableCollection<TaskCenterTaskItem> DailyTasks { get; } = new();
        public ObservableCollection<TaskCenterTaskItem> OneTimeTasks { get; } = new();
        public ObservableCollection<TaskCenterTaskItem> LongTermTasks { get; } = new();
        public ObservableCollection<PointTransaction> Transactions { get; } = new();
        public ObservableCollection<PointStoreItem> VirtualStoreItems { get; } = new();
        public ObservableCollection<PointStoreItem> PhysicalStoreItems { get; } = new();
        public ObservableCollection<ExchangeRecord> ExchangeRecords { get; } = new();
        public ObservableCollection<InviteRewardRecord> InviteRecords { get; } = new();

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
        public PaymentOrder? LatestPaymentOrder
        {
            get => _latestPaymentOrder;
            private set
            {
                if (_latestPaymentOrder == value) return;
                _latestPaymentOrder = value;
                OnPropertyChanged();
            }
        }

        public int Points => _document.Points;
        public bool IsPermanentVip => _document.IsPermanentVip;
        public bool CanPurchaseMembership => !IsPermanentVip;
        public string TodayPointsText => _document.TodayPointsText;
        public string EarnedPointsText => _document.EarnedPointsText;
        public string SpentPointsText => _document.SpentPointsText;
        public int SignInStreak => _document.SignInStreak;
        public string SignInStreakText => $"已连续 {SignInStreak} 天";
        public bool HasSignedInToday => _document.HasSignedInToday;
        public string SignInButtonText => HasSignedInToday ? "已签到，明日再来" : "签到领积分";
        public string DailyTaskSummary => $"{_document.DailyCompletedCount}/{_document.DailyTotalCount}";
        public string OneTimeTaskSummary => $"{_document.OneTimeCompletedCount}/{_document.OneTimeTotalCount}";
        public string LongTermTaskSummary => $"{_document.LongTermCompletedCount}/{_document.LongTermTotalCount}";
        public bool HasTransactions => Transactions.Count > 0;
        public bool IsTransactionsEmpty => !HasTransactions;
        public bool HasExchangeRecords => ExchangeRecords.Count > 0;
        public bool IsExchangeRecordsEmpty => !HasExchangeRecords;
        public bool HasInviteRecords => InviteRecords.Count > 0;
        public bool IsInviteRecordsEmpty => !HasInviteRecords;
        public string InviteCode => _document.InviteCode;
        public bool HasInviteCode => !string.IsNullOrWhiteSpace(InviteCode);
        public string InviteCodeText => HasInviteCode ? InviteCode : "暂无邀请码";
        public string InviteShareText => $"我在花火漫画等你，注册时填写邀请码 {InviteCode} 即可绑定邀请关系。";
        public int InvitedCount => _document.InvitedCount;
        public int SuccessfulInviteCount => _document.SuccessfulInviteCount;
        public int PendingCheckinInviteCount => _document.PendingCheckinInviteCount;
        public int InvitePoints => _document.InvitePoints;
        public string InvitedCountText => InvitedCount.ToString();
        public string SuccessfulInviteCountText => SuccessfulInviteCount.ToString();
        public string PendingCheckinInviteCountText => PendingCheckinInviteCount.ToString();
        public string InvitePointsText => $"+{InvitePoints}";

        public async Task LoadAsync()
        {
            IsLoading = true;
            ErrorMessage = null;

            try
            {
                UpdateFromDocument(await TaskCenterService.Instance.GetTaskCenterAsync(preferCached: true));
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

        public async Task<PaymentOrder?> CreatePaymentOrderAsync(string productId)
        {
            if (string.IsNullOrWhiteSpace(productId)) return null;

            IsWorking = true;
            ErrorMessage = null;

            try
            {
                LatestPaymentOrder = await SupabaseService.Instance.CreatePaymentOrderAsync(productId);
                FeedbackMessage = "订单已创建";
                return LatestPaymentOrder;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                return null;
            }
            finally
            {
                IsWorking = false;
            }
        }

        public async Task<PaymentOrder?> SyncPaymentOrderAsync(PaymentOrder order)
        {
            IsWorking = true;
            ErrorMessage = null;

            try
            {
                LatestPaymentOrder = await SupabaseService.Instance.QueryPaymentOrderAsync(
                    order.Id,
                    order.TradeNo,
                    syncHypay: true);
                FeedbackMessage = LatestPaymentOrder.IsPaid ? "支付已确认" : "订单仍在待支付";
                if (LatestPaymentOrder.IsPaid)
                {
                    UpdateFromDocument(await TaskCenterService.Instance.GetTaskCenterAsync());
                }

                return LatestPaymentOrder;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                return null;
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
            Replace(InviteRecords, document.InviteRecords);
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
            OnPropertyChanged(nameof(IsPermanentVip));
            OnPropertyChanged(nameof(CanPurchaseMembership));
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
            OnPropertyChanged(nameof(HasInviteRecords));
            OnPropertyChanged(nameof(IsInviteRecordsEmpty));
            OnPropertyChanged(nameof(InviteCode));
            OnPropertyChanged(nameof(HasInviteCode));
            OnPropertyChanged(nameof(InviteCodeText));
            OnPropertyChanged(nameof(InviteShareText));
            OnPropertyChanged(nameof(InvitedCount));
            OnPropertyChanged(nameof(SuccessfulInviteCount));
            OnPropertyChanged(nameof(PendingCheckinInviteCount));
            OnPropertyChanged(nameof(InvitePoints));
            OnPropertyChanged(nameof(InvitedCountText));
            OnPropertyChanged(nameof(SuccessfulInviteCountText));
            OnPropertyChanged(nameof(PendingCheckinInviteCountText));
            OnPropertyChanged(nameof(InvitePointsText));
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
