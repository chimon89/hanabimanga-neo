using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace hanabimanga.Models
{
    // 工单（资源与反馈）相关模型。

    public static class FeedbackOptions
    {
        public const string DomainDev = "DEV";
        public const string DomainOps = "OPS";
        public const string CategoryBookRequest = "求书";
        public const string CategoryMissing = "资源缺失";
        public const string CategoryMismatch = "资源不匹配";

        public static readonly IReadOnlyList<string> DevCategories =
            new[] { "新功能意见", "BUG反馈", "其他" };

        public static readonly IReadOnlyList<string> OpsCategories =
            new[] { "求书", "资源缺失", "资源不匹配", "其他" };

        public static bool RequiresComicAssociation(string? category)
            => category is CategoryMissing or CategoryMismatch;
    }

    // 列表卡片绑定的工单 UI 模型。VoteCount / HasVoted 可变以支持点击共鸣即时刷新。
    public sealed class FeedbackTicket : INotifyPropertyChanged
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public string Domain { get; set; } = "OPS";
        public string Category { get; set; } = "其他";
        public string Status { get; set; } = "RECORDED";
        public int Priority { get; set; }
        public string? AdminResponse { get; set; }
        public string ReporterId { get; set; } = "";
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? BangumiName { get; set; }
        public string? ComicTitle { get; set; }

        public string ReporterName { get; set; } = "花火用户";
        public string? ReporterAvatarUrl { get; set; }
        public bool IsOwn { get; set; }

        private int _voteCount;
        public int VoteCount
        {
            get => _voteCount;
            set
            {
                if (_voteCount == value) return;
                _voteCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VoteCountText));
            }
        }

        private bool _hasVoted;
        public bool HasVoted
        {
            get => _hasVoted;
            set
            {
                if (_hasVoted == value) return;
                _hasVoted = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VoteGlyph));
            }
        }

        public bool CanVote => !IsOwn;
        public string AvatarPreviewUrl => ToAvatarPreviewUrl(ReporterAvatarUrl);
        public string VoteCountText => $"+{VoteCount}";
        // Segoe MDL2:  HeartFill,  Heart
        public string VoteGlyph => HasVoted ? "" : "";
        public string CreatedAtText => FormatCreatedAt(CreatedAt);
        public string DomainText => Domain == FeedbackOptions.DomainDev ? "功能反馈" : "资源反馈";
        public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
        public bool IsReplied => !string.IsNullOrWhiteSpace(AdminResponse);

        public string StatusText => Status switch
        {
            "RECORDED" => "已记录",
            "TRACKING" => "跟踪中",
            "IN_PROGRESS" => "进行中",
            "EVALUATING" => "评估中",
            "COMPLETED" => "已完成",
            "DEFERRED" => "已搁置",
            "CANCELLED" => "已取消",
            _ => Status,
        };

        public string? RelationText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(BangumiName)) return $"Bangumi · {BangumiName}";
                if (!string.IsNullOrWhiteSpace(ComicTitle)) return $"关联 · {ComicTitle}";
                return null;
            }
        }

        public bool HasRelation => !string.IsNullOrWhiteSpace(RelationText);

        private static string FormatCreatedAt(DateTime? createdAt)
        {
            if (createdAt == null) return "";

            var localTime = createdAt.Value.Kind == DateTimeKind.Utc
                ? createdAt.Value.ToLocalTime()
                : createdAt.Value;
            var elapsed = DateTime.Now - localTime;

            if (elapsed.TotalMinutes < 1) return "刚刚";
            if (elapsed.TotalHours < 1) return $"{Math.Max(1, (int)elapsed.TotalMinutes)} 分钟前";
            if (elapsed.TotalDays < 1) return $"{Math.Max(1, (int)elapsed.TotalHours)} 小时前";
            if (elapsed.TotalDays < 7) return $"{Math.Max(1, (int)elapsed.TotalDays)} 天前";

            return localTime.ToString("yyyy-MM-dd");
        }

        private static string ToAvatarPreviewUrl(string? avatarUrl)
        {
            if (string.IsNullOrWhiteSpace(avatarUrl))
            {
                return "ms-appx:///Assets/avatar/ic_avatar_default.webp";
            }

            if (Uri.TryCreate(avatarUrl, UriKind.Absolute, out _))
            {
                return avatarUrl;
            }

            var fileName = Path.GetFileName(avatarUrl.Trim().Replace('\\', '/'));
            return string.IsNullOrWhiteSpace(fileName)
                ? "ms-appx:///Assets/avatar/ic_avatar_default.webp"
                : $"ms-appx:///Assets/avatar/{fileName}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // Bangumi 搜索结果项（用于求书选条目）。
    public sealed class BangumiBook
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string? NameCn { get; set; }
        public string? CoverUrl { get; set; }

        public string DisplayName => string.IsNullOrWhiteSpace(NameCn) ? Name : NameCn!;
        public string SubText => string.IsNullOrWhiteSpace(NameCn)
            ? $"Bangumi ID {Id}"
            : $"原名 {Name} · ID {Id}";
        public string CoverPreviewUrl => string.IsNullOrWhiteSpace(CoverUrl)
            ? "ms-appx:///Assets/avatar/ic_avatar_default.webp"
            : CoverUrl!;
    }

    // get_ticket_quota 返回的配额信息。
    public sealed class TicketQuota
    {
        public bool IsVip { get; set; }
        public int OpenCount { get; set; }
        public int MaxOpen { get; set; }
        public int Remaining { get; set; }
        public bool IsBanned { get; set; }
        public bool IsCoolingDown { get; set; }
        public DateTime? RetryAfter { get; set; }
        public DateTime? BannedUntil { get; set; }
    }

    internal sealed class RawTicketRecord
    {
        [JsonProperty("id")]
        public string? Id { get; set; }

        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("description")]
        public string? Description { get; set; }

        [JsonProperty("domain")]
        public string? Domain { get; set; }

        [JsonProperty("category")]
        public string? Category { get; set; }

        [JsonProperty("status")]
        public string? Status { get; set; }

        [JsonProperty("priority")]
        public int? Priority { get; set; }

        [JsonProperty("vote_count")]
        public int? VoteCount { get; set; }

        [JsonProperty("meta_info")]
        public JObject? MetaInfo { get; set; }

        [JsonProperty("admin_response")]
        public string? AdminResponse { get; set; }

        [JsonProperty("reporter_id")]
        public string? ReporterId { get; set; }

        [JsonProperty("created_at")]
        public DateTime? CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public DateTime? UpdatedAt { get; set; }
    }

    internal sealed class RawTicketVoteRecord
    {
        [JsonProperty("ticket_id")]
        public string? TicketId { get; set; }
    }
}
