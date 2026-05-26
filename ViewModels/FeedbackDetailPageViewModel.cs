using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using hanabimanga.Models;
using hanabimanga.Services;

namespace hanabimanga.ViewModels
{
    public sealed class FeedbackDetailPageViewModel : INotifyPropertyChanged
    {
        private FeedbackTicket? _ticket;
        private bool _isLoading;
        private bool _isVoting;
        private string? _errorMessage;

        public ObservableCollection<TicketVoter> Voters { get; } = new();

        public FeedbackDetailPageViewModel()
        {
            Voters.CollectionChanged += Voters_CollectionChanged;
        }

        public FeedbackTicket? Ticket => _ticket;
        public bool HasTicket => _ticket != null;

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading == value) return;
                _isLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowEmptyVoters));
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
                OnPropertyChanged(nameof(ShowEmptyVoters));
            }
        }

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public string VoteCountText => (_ticket?.VoteCount ?? 0).ToString();
        public string VoterCountText => $"{Voters.Count} 人";
        public bool HasVoters => Voters.Count > 0;
        public bool ShowEmptyVoters => !IsLoading && Voters.Count == 0 && !HasError;
        public bool CanVote => _ticket is { IsOwn: false } && !_isVoting;
        public bool IsOwnTicket => _ticket?.IsOwn == true;
        public bool IsBookRequest => _ticket?.IsBookRequest == true;
        public string VoteCountLabel => IsBookRequest ? "想看人数" : "受影响人数";
        public string VoteUsersLabel => IsBookRequest ? "想看用户" : "共鸣用户";
        public string EmptyVotersText => IsBookRequest ? "还没有人想看,你可以第一个" : "还没有人共鸣,你可以第一个";
        public string VoteButtonText => _ticket?.VoteActionText ?? "我也遇到了";
        public string VoteGlyph => _ticket?.VoteGlyph ?? "";

        public void Initialize(FeedbackTicket ticket)
        {
            if (_ticket != null)
            {
                _ticket.PropertyChanged -= Ticket_PropertyChanged;
            }

            _ticket = ticket;
            _ticket.PropertyChanged += Ticket_PropertyChanged;

            OnPropertyChanged(nameof(Ticket));
            OnPropertyChanged(nameof(HasTicket));
            OnPropertyChanged(nameof(VoteCountText));
            OnPropertyChanged(nameof(CanVote));
            OnPropertyChanged(nameof(IsOwnTicket));
            OnPropertyChanged(nameof(IsBookRequest));
            OnPropertyChanged(nameof(VoteCountLabel));
            OnPropertyChanged(nameof(VoteUsersLabel));
            OnPropertyChanged(nameof(EmptyVotersText));
            OnPropertyChanged(nameof(VoteButtonText));
            OnPropertyChanged(nameof(VoteGlyph));
        }

        public async Task LoadVotersAsync()
        {
            if (_ticket == null || IsLoading) return;

            IsLoading = true;
            ErrorMessage = null;

            try
            {
                var voters = await SupabaseService.Instance.GetTicketVotersAsync(_ticket.Id);
                Voters.Clear();
                foreach (var voter in voters)
                {
                    Voters.Add(voter);
                }

                RefreshVoterProperties();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"共鸣用户加载失败:{ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task ToggleVoteAsync()
        {
            if (_ticket == null || _ticket.IsOwn || _isVoting) return;

            _isVoting = true;
            OnPropertyChanged(nameof(CanVote));
            var wasVoted = _ticket.HasVoted;
            _ticket.HasVoted = !wasVoted;
            _ticket.VoteCount += wasVoted ? -1 : 1;

            try
            {
                if (wasVoted)
                {
                    await SupabaseService.Instance.RemoveTicketVoteAsync(_ticket.Id);
                }
                else
                {
                    await SupabaseService.Instance.AddTicketVoteAsync(_ticket.Id);
                }

                await LoadVotersAsync();
            }
            catch (Exception ex)
            {
                _ticket.HasVoted = wasVoted;
                _ticket.VoteCount += wasVoted ? 1 : -1;
                ErrorMessage = $"共鸣操作失败:{ex.Message}";
            }
            finally
            {
                _isVoting = false;
                OnPropertyChanged(nameof(CanVote));
            }
        }

        private void Ticket_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FeedbackTicket.VoteCount))
            {
                OnPropertyChanged(nameof(VoteCountText));
            }
            else if (e.PropertyName == nameof(FeedbackTicket.HasVoted))
            {
                OnPropertyChanged(nameof(VoteButtonText));
                OnPropertyChanged(nameof(VoteGlyph));
            }
            else if (e.PropertyName == nameof(FeedbackTicket.VoteActionText))
            {
                OnPropertyChanged(nameof(VoteButtonText));
            }
            else if (e.PropertyName == nameof(FeedbackTicket.VoteGlyph))
            {
                OnPropertyChanged(nameof(VoteGlyph));
            }
        }

        private void Voters_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RefreshVoterProperties();
        }

        private void RefreshVoterProperties()
        {
            OnPropertyChanged(nameof(VoterCountText));
            OnPropertyChanged(nameof(HasVoters));
            OnPropertyChanged(nameof(ShowEmptyVoters));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
