using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace hanabimanga.Models
{
    // Postgrest / Realtime 模型,对应 notifications 表。
    [Table("notifications")]
    public class NotificationRecord : BaseModel
    {
        [PrimaryKey("id", false)]
        public string Id { get; set; } = "";

        [Column("user_id")]
        public string UserId { get; set; } = "";

        [Column("type")]
        public string Type { get; set; } = "";

        [Column("title")]
        public string Title { get; set; } = "";

        [Column("body")]
        public string? Body { get; set; }

        [Column("is_read")]
        public bool IsRead { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }

    // 通知 UI 模型;IsRead 可变,用于标记已读后即时刷新未读圆点。
    public sealed class NotificationItem : INotifyPropertyChanged
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Body { get; set; }
        public string Type { get; set; } = "";
        public DateTime CreatedAt { get; set; }

        public bool HasBody => !string.IsNullOrWhiteSpace(Body);
        public string TimeText => FormatRelative(CreatedAt);

        private bool _isRead;
        public bool IsRead
        {
            get => _isRead;
            set
            {
                if (_isRead == value) return;
                _isRead = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsUnread));
            }
        }

        public bool IsUnread => !IsRead;

        private static string FormatRelative(DateTime time)
        {
            var span = DateTime.UtcNow - time.ToUniversalTime();
            if (span < TimeSpan.FromMinutes(1)) return "刚刚";
            if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} 分钟前";
            if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} 小时前";
            if (span < TimeSpan.FromDays(30)) return $"{(int)span.TotalDays} 天前";
            return time.ToLocalTime().ToString("yyyy-MM-dd");
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
