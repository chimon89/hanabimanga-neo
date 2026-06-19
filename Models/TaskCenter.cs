using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace hanabimanga.Models
{
    public sealed class TaskCenterDocument
    {
        public int Points { get; set; }
        public int TodayPoints { get; set; }
        public int EarnedPoints { get; set; }
        public int SpentPoints { get; set; }
        public int SignInStreak { get; set; }
        public bool HasSignedInToday { get; set; }
        public bool IsPermanentVip { get; set; }
        public string InviteCode { get; set; } = "";
        public int InvitedCount { get; set; }
        public int SuccessfulInviteCount { get; set; }
        public int PendingCheckinInviteCount { get; set; }
        public int InvitePoints { get; set; }
        public List<TaskCenterSignInDay> SignInDays { get; set; } = new();
        public List<TaskCenterTaskItem> DailyTasks { get; set; } = new();
        public List<TaskCenterTaskItem> OneTimeTasks { get; set; } = new();
        public List<TaskCenterTaskItem> LongTermTasks { get; set; } = new();
        public List<PointTransaction> Transactions { get; set; } = new();
        public List<PointStoreItem> StoreItems { get; set; } = new();
        public List<ExchangeRecord> ExchangeRecords { get; set; } = new();
        public List<InviteRewardRecord> InviteRecords { get; set; } = new();

        public int DailyCompletedCount => DailyTasks.Count(task => task.IsCompleted);
        public int OneTimeCompletedCount => OneTimeTasks.Count(task => task.IsCompleted);
        public int LongTermCompletedCount => LongTermTasks.Count(task => task.IsCompleted);
        public int DailyTotalCount => DailyTasks.Count;
        public int OneTimeTotalCount => OneTimeTasks.Count;
        public int LongTermTotalCount => LongTermTasks.Count;
        public string TodayPointsText => TodayPoints > 0 ? $"+{TodayPoints}" : "0";
        public string EarnedPointsText => $"+{EarnedPoints}";
        public string SpentPointsText => SpentPoints.ToString();
    }

    public sealed class TaskCenterSignInDay
    {
        public string Label { get; set; } = "";
        public int Points { get; set; }
        public bool IsChecked { get; set; }
        public bool IsToday { get; set; }
        public string PointsText => $"+{Points}";
        public string StatusGlyph => IsChecked ? "\uE73E" : "";
        public double Opacity => IsChecked || IsToday ? 1 : 0.48;
    }

    public sealed class TaskCenterTaskItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public int RewardPoints { get; set; }
        public int Current { get; set; }
        public int Target { get; set; } = 1;
        public bool IsCompleted { get; set; }
        public bool IsClaimed { get; set; }
        public bool IsRepeatable { get; set; }
        public string IconGlyph { get; set; } = "\uE8F1";

        public double ProgressValue => Target <= 0 ? 0 : Math.Clamp(Current * 100.0 / Target, 0, 100);
        public string ProgressText => Target <= 0 ? "" : $"{Math.Min(Current, Target)}/{Target}";
        public string RewardText => $"{RewardPoints} 积分";
        public string StatusText => IsClaimed
            ? "已完成"
            : IsCompleted
                ? "可领取"
                : IsRepeatable
                    ? "不限次"
                    : "进行中";
    }

    public sealed class PointTransaction
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string TimeText { get; set; } = "";
        public int Amount { get; set; }
        public string Type { get; set; } = "income";

        public bool IsIncome => Amount >= 0;
        public string AmountText => Amount >= 0 ? $"+{Amount}" : Amount.ToString();
        public string IconGlyph => IsIncome ? "\uE710" : "\uE738";
    }

    public sealed class PointStoreItem
    {
        public string Id { get; set; } = "";
        public string Category { get; set; } = "virtual";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public int Points { get; set; }
        public decimal Price { get; set; }
        public int DurationDays { get; set; }
        public int Stock { get; set; }
        public string? ImageUrl { get; set; }
        public string IconGlyph { get; set; } = "\uE7BF";
        public int AvailablePoints { get; set; }

        public bool IsVirtual => Category == "virtual";
        public bool IsPhysical => Category == "physical";
        public bool HasImage => !string.IsNullOrWhiteSpace(ImageUrl);
        public bool CanRedeem => AvailablePoints >= Points && Points > 0;
        public string PointsText => $"{Points} 积分";
        public string PriceText => Price > 0 ? $"¥{Price:0.##}" : PointsText;
        public string DurationText => DurationDays > 0 ? $"{DurationDays} 天" : "";
        public string StockText => Stock > 0 ? $"剩余 {Stock}" : "";
        public string ActionText => IsVirtual ? "购买" : "查看详情";
    }

    public sealed class ExchangeRecord
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public int Points { get; set; }
        public DateTime CreatedAt { get; set; }
        public string StatusText { get; set; } = "处理中";
        public string TimeText => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string PointsText => $"{Points} 积分";
    }

    public sealed class InviteRewardRecord
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "邀请奖励";
        public int Points { get; set; }
        public DateTime CreatedAt { get; set; }
        public string StatusText { get; set; } = "奖励已发放";
        public string TimeText => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string PointsText => Points > 0 ? $"+{Points}" : Points.ToString();
    }

    internal sealed class RawPointLedgerRecord
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("user_id")]
        public string? UserId { get; set; }

        [JsonProperty("amount")]
        public int Amount { get; set; }

        [JsonProperty("reason")]
        public string? Reason { get; set; }

        [JsonProperty("created_at")]
        public DateTime? CreatedAt { get; set; }
    }

    internal sealed class RawTaskDefinitionRecord
    {
        [JsonProperty("id")]
        public string? Id { get; set; }

        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("description")]
        public string? Description { get; set; }

        [JsonProperty("category")]
        public string? Category { get; set; }

        [JsonProperty("task_type")]
        public string? TaskType { get; set; }

        [JsonProperty("is_active")]
        public bool? IsActive { get; set; }

        [JsonProperty("sort_order")]
        public int? SortOrder { get; set; }

        [JsonProperty("created_at")]
        public DateTime? CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public DateTime? UpdatedAt { get; set; }
    }

    internal sealed class RawUserTaskProgressRecord
    {
        [JsonProperty("user_id")]
        public string? UserId { get; set; }

        [JsonProperty("task_id")]
        public string? TaskId { get; set; }

        [JsonProperty("period_key")]
        public string? PeriodKey { get; set; }
    }

    internal sealed class RawPointProductRecord
    {
        [JsonProperty("id")]
        public string? Id { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("description")]
        public string? Description { get; set; }

        [JsonProperty("price")]
        public decimal Price { get; set; }

        [JsonProperty("image_url")]
        public string? ImageUrl { get; set; }

        [JsonProperty("duration_days")]
        public int? DurationDays { get; set; }

        [JsonProperty("is_active")]
        public bool? IsActive { get; set; }

        [JsonProperty("sort_order")]
        public int? SortOrder { get; set; }

        [JsonProperty("stock_limit")]
        public int? StockLimit { get; set; }

        [JsonProperty("sales_count")]
        public int? SalesCount { get; set; }

        [JsonProperty("type")]
        public string? Type { get; set; }
    }

    internal sealed class RawCheckinWeekPreview
    {
        [JsonProperty("days")]
        public List<RawCheckinDay>? Days { get; set; }

        [JsonProperty("today")]
        public string? Today { get; set; }

        [JsonProperty("week_start")]
        public string? WeekStart { get; set; }

        [JsonProperty("week_end")]
        public string? WeekEnd { get; set; }
    }

    internal sealed class RawCheckinDay
    {
        [JsonProperty("date")]
        public string? Date { get; set; }

        [JsonProperty("state")]
        public string? State { get; set; }

        [JsonProperty("points")]
        public int Points { get; set; }

        [JsonProperty("streak")]
        public int Streak { get; set; }

        [JsonProperty("day_of_week")]
        public int DayOfWeek { get; set; }
    }

    internal sealed class RawChapterViewLogRecord
    {
        [JsonProperty("user_id")]
        public string? UserId { get; set; }

        [JsonProperty("chapter_id")]
        public long ChapterId { get; set; }

        [JsonProperty("viewed_at")]
        public DateTime? ViewedAt { get; set; }
    }
}
