using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.Services;

namespace hanabimanga.ViewModels
{
    public sealed class FeedbackListPageViewModel : INotifyPropertyChanged
    {
        private bool _isLoading;
        private string? _errorMessage;
        private readonly HashSet<string> _votingIds = new(StringComparer.Ordinal);

        public ObservableCollection<FeedbackTicket> Tickets { get; } = new();

        // 当前筛选条件;null 表示「全部」。
        public string? SelectedDomain { get; set; }
        public string? SelectedStatus { get; set; }
        public string SortKey { get; set; } = "votes";

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
                OnPropertyChanged(nameof(IsEmpty));
            }
        }

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public bool HasTickets => Tickets.Count > 0;
        public bool IsEmpty => !IsLoading && !HasTickets && !HasError;

        public async Task LoadAsync()
        {
            if (IsLoading) return;

            if (!SupabaseService.Instance.IsInitialized)
            {
                ErrorMessage = "Supabase 未初始化,无法加载反馈列表。";
                return;
            }

            IsLoading = true;
            ErrorMessage = null;

            try
            {
                var votedIds = await SupabaseService.Instance.GetMyTicketVoteIdsAsync();
                var tickets = await SupabaseService.Instance.GetFeedbackTicketsAsync(
                    SelectedDomain, SelectedStatus, SortKey);

                Tickets.Clear();
                foreach (var ticket in tickets)
                {
                    ticket.HasVoted = votedIds.Contains(ticket.Id);
                    Tickets.Add(ticket);
                }

                RefreshListProperties();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"反馈列表加载失败:{ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task ToggleVoteAsync(FeedbackTicket ticket)
        {
            if (ticket.IsOwn || _votingIds.Contains(ticket.Id)) return;

            _votingIds.Add(ticket.Id);
            var wasVoted = ticket.HasVoted;
            ticket.HasVoted = !wasVoted;
            ticket.VoteCount += wasVoted ? -1 : 1;

            try
            {
                if (wasVoted)
                {
                    await SupabaseService.Instance.RemoveTicketVoteAsync(ticket.Id);
                }
                else
                {
                    await SupabaseService.Instance.AddTicketVoteAsync(ticket.Id);
                }
            }
            catch (Exception ex)
            {
                ticket.HasVoted = wasVoted;
                ticket.VoteCount += wasVoted ? 1 : -1;
                ErrorMessage = $"共鸣操作失败:{ex.Message}";
            }
            finally
            {
                _votingIds.Remove(ticket.Id);
            }
        }

        private void RefreshListProperties()
        {
            OnPropertyChanged(nameof(HasTickets));
            OnPropertyChanged(nameof(IsEmpty));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
