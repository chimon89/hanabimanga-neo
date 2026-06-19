using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using hanabimanga.Models;

namespace hanabimanga.Services
{
    public sealed class TaskCenterService
    {
        public static TaskCenterService Instance { get; } = new();

        private readonly object _syncRoot = new();
        private TaskCenterDocument _document;
        private bool _hasRemoteDocument;
        private string? _cachedUserId;
        private Task<TaskCenterDocument?>? _preloadTask;

        private TaskCenterService()
        {
            _document = CreateSeedDocument();
        }

        public async Task PreloadAsync()
        {
            if (!SupabaseService.Instance.IsSignedIn)
            {
                ClearCache();
                return;
            }

            var userId = SupabaseService.Instance.CurrentUserId;
            lock (_syncRoot)
            {
                if (!string.Equals(_cachedUserId, userId, StringComparison.Ordinal))
                {
                    _document = CreateSeedDocument();
                    _hasRemoteDocument = false;
                    _cachedUserId = userId;
                    _preloadTask = null;
                }

                if (_preloadTask is { IsCompleted: false })
                {
                    return;
                }

                _preloadTask = FetchAndCacheRemoteAsync();
            }

            try
            {
                await _preloadTask;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[task-center] preload failed: {ex.Message}");
            }
        }

        public Task<TaskCenterDocument> GetTaskCenterAsync(bool preferCached = false)
        {
            return GetTaskCenterCoreAsync(preferCached);
        }

        public void ClearCache()
        {
            lock (_syncRoot)
            {
                _document = CreateSeedDocument();
                _hasRemoteDocument = false;
                _cachedUserId = null;
                _preloadTask = null;
            }
        }

        private async Task<TaskCenterDocument> GetTaskCenterCoreAsync(bool preferCached)
        {
            if (preferCached)
            {
                Task<TaskCenterDocument?>? preloadTask;
                lock (_syncRoot)
                {
                    if (_hasRemoteDocument)
                    {
                        RefreshStoreAffordability(_document);
                        return Clone(_document);
                    }

                    preloadTask = _preloadTask;
                }

                if (preloadTask is { IsCompleted: false })
                {
                    try
                    {
                        var warmed = await preloadTask;
                        if (warmed != null) return Clone(warmed);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[task-center] awaiting preload failed: {ex.Message}");
                    }
                }
            }

            try
            {
                var remoteDocument = await FetchAndCacheRemoteAsync();
                if (remoteDocument != null)
                {
                    return remoteDocument;
                }
            }
            catch (InvalidOperationException ex) when (IsAuthenticationRequired(ex))
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[task-center] using local fallback: {ex.Message}");
            }

            lock (_syncRoot)
            {
                RefreshStoreAffordability(_document);
                return Clone(_document);
            }
        }

        private async Task<TaskCenterDocument?> FetchAndCacheRemoteAsync()
        {
            var remoteDocument = await SupabaseService.Instance.GetCurrentUserTaskCenterAsync();
            if (remoteDocument != null)
            {
                lock (_syncRoot)
                {
                    _document = Clone(remoteDocument);
                    _hasRemoteDocument = true;
                    _cachedUserId = SupabaseService.Instance.CurrentUserId;
                }
            }

            return remoteDocument;
        }

        public async Task<TaskCenterDocument> ClaimDailySignInAsync()
        {
            await SupabaseService.Instance.ClaimCheckinRewardAsync();
            // 签到结果与积分以服务器为准,领取后重新拉取完整的任务中心文档。
            return await GetTaskCenterCoreAsync(preferCached: false);
        }

        public Task<TaskCenterDocument> RedeemAsync(string itemId)
        {
            lock (_syncRoot)
            {
                var item = _document.StoreItems.FirstOrDefault(storeItem => storeItem.Id == itemId);
                if (item == null || item.Points <= 0 || _document.Points < item.Points)
                {
                    return Task.FromResult(Clone(_document));
                }

                _document.Points -= item.Points;
                _document.SpentPoints += item.Points;
                if (item.Stock > 0)
                {
                    item.Stock--;
                }

                var record = new ExchangeRecord
                {
                    Id = $"exchange-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    Title = item.Title,
                    Points = item.Points,
                    CreatedAt = DateTime.UtcNow,
                    StatusText = item.IsVirtual ? "已发放" : "待确认",
                };
                _document.ExchangeRecords.Insert(0, record);
                _document.Transactions.Insert(0, new PointTransaction
                {
                    Id = $"redeem-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    Title = $"兑换 {item.Title}",
                    TimeText = "刚刚",
                    Amount = -item.Points,
                    Type = "expense",
                });

                RefreshStoreAffordability(_document);
                return Task.FromResult(Clone(_document));
            }
        }

        private static TaskCenterDocument CreateSeedDocument()
        {
            var document = new TaskCenterDocument
            {
                Points = 245,
                TodayPoints = 170,
                EarnedPoints = 245,
                SpentPoints = 0,
                SignInStreak = 2,
                HasSignedInToday = true,
                SignInDays =
                {
                    new TaskCenterSignInDay { Label = "周一", Points = 10 },
                    new TaskCenterSignInDay { Label = "周二", Points = 15, IsChecked = true },
                    new TaskCenterSignInDay { Label = "周三", Points = 20, IsChecked = true },
                    new TaskCenterSignInDay { Label = "周四", Points = 10 },
                    new TaskCenterSignInDay { Label = "周五", Points = 15 },
                    new TaskCenterSignInDay { Label = "周六", Points = 15, IsChecked = true },
                    new TaskCenterSignInDay { Label = "周日", Points = 20, IsChecked = true, IsToday = true },
                },
                DailyTasks =
                {
                    new TaskCenterTaskItem
                    {
                        Id = "daily-read",
                        Title = "每日阅读",
                        Description = "再读 8 页得 25 积分",
                        RewardPoints = 25,
                        Current = 12,
                        Target = 20,
                        IconGlyph = "\uE8F1",
                    },
                    new TaskCenterTaskItem
                    {
                        Id = "daily-comment",
                        Title = "每日评论",
                        Description = "再留 2 条得 15 积分",
                        RewardPoints = 15,
                        Current = 0,
                        Target = 2,
                        IconGlyph = "\uE8F2",
                    },
                },
                OneTimeTasks =
                {
                    new TaskCenterTaskItem
                    {
                        Id = "verify-email",
                        Title = "验证邮箱",
                        Description = "验证你的邮箱以解锁邀请奖励等功能",
                        RewardPoints = 100,
                        Current = 1,
                        Target = 1,
                        IsCompleted = true,
                        IsClaimed = true,
                        IconGlyph = "\uE73E",
                    },
                },
                LongTermTasks =
                {
                    new TaskCenterTaskItem
                    {
                        Id = "invite-friend",
                        Title = "邀请好友",
                        Description = "完成 1 次得 800 积分",
                        RewardPoints = 800,
                        Current = 0,
                        Target = 1,
                        IsRepeatable = true,
                        IconGlyph = "\uE8F8",
                    },
                },
                Transactions =
                {
                    new PointTransaction { Id = "tx-1", Title = "每日阅读", TimeText = "29 分钟前", Amount = 25 },
                    new PointTransaction { Id = "tx-2", Title = "每日阅读", TimeText = "56 分钟前", Amount = 25 },
                    new PointTransaction { Id = "tx-3", Title = "每日签到", TimeText = "4 小时前", Amount = 20 },
                    new PointTransaction { Id = "tx-4", Title = "每日阅读", TimeText = "23 小时前", Amount = 25 },
                    new PointTransaction { Id = "tx-5", Title = "每日签到", TimeText = "1 天前", Amount = 15 },
                    new PointTransaction { Id = "tx-6", Title = "每日签到", TimeText = "3 天前", Amount = 20 },
                    new PointTransaction { Id = "tx-7", Title = "每日签到", TimeText = "4 天前", Amount = 15 },
                    new PointTransaction { Id = "tx-8", Title = "验证邮箱", TimeText = "4 天前", Amount = 100 },
                },
                StoreItems =
                {
                    new PointStoreItem
                    {
                        Id = "vip-7d",
                        Category = "virtual",
                        Title = "7 天 VIP",
                        Description = "使用 800 积分兑换 7 天 VIP 会员",
                        Points = 800,
                        IconGlyph = "\uE7BF",
                    },
                    new PointStoreItem
                    {
                        Id = "vip-1d",
                        Category = "virtual",
                        Title = "1 天 VIP",
                        Description = "使用 250 积分兑换 1 天 VIP 会员",
                        Points = 250,
                        IconGlyph = "\uE7BF",
                    },
                    new PointStoreItem
                    {
                        Id = "acrylic-charm",
                        Category = "physical",
                        Title = "亚克力挂件",
                        Description = "使用 1200 积分兑换一枚亚克力挂件",
                        Points = 1200,
                        Stock = 997,
                        IconGlyph = "\uE7C3",
                    },
                    new PointStoreItem
                    {
                        Id = "towel",
                        Category = "physical",
                        Title = "麻薯素毛毯",
                        Description = "使用 4000 积分兑换三袋麻薯素毛毯",
                        Points = 4000,
                        Stock = 999,
                        IconGlyph = "\uE790",
                    },
                    new PointStoreItem
                    {
                        Id = "cookies",
                        Category = "physical",
                        Title = "曲奇饼干",
                        Description = "使用 8000 积分兑换三袋曲奇饼干",
                        Points = 8000,
                        Stock = 998,
                        IconGlyph = "\uE7C1",
                    },
                    new PointStoreItem
                    {
                        Id = "desk-pad",
                        Category = "physical",
                        Title = "动漫鼠标垫",
                        Description = "使用 12000 积分兑换一张动漫鼠标垫",
                        Points = 12000,
                        Stock = 996,
                        IconGlyph = "\uE8A7",
                    },
                    new PointStoreItem
                    {
                        Id = "food-box",
                        Category = "physical",
                        Title = "哈基米南北绿豆浆",
                        Description = "使用 13800 积分兑换一箱限定饮品",
                        Points = 13800,
                        Stock = 998,
                        IconGlyph = "\uE8D4",
                    },
                    new PointStoreItem
                    {
                        Id = "figure-custom",
                        Category = "physical",
                        Title = "128 元内手办自选",
                        Description = "使用 80000 积分兑换 128 元以内的手办",
                        Points = 80000,
                        Stock = 998,
                        IconGlyph = "\uE7C3",
                    },
                },
            };

            RefreshStoreAffordability(document);
            return document;
        }

        private static bool IsAuthenticationRequired(Exception ex)
            => ex.Message.StartsWith("请先登录", StringComparison.Ordinal);

        private static void RefreshStoreAffordability(TaskCenterDocument document)
        {
            foreach (var item in document.StoreItems)
            {
                item.AvailablePoints = document.Points;
            }
        }

        private static TaskCenterDocument Clone(TaskCenterDocument source)
        {
            return new TaskCenterDocument
            {
                Points = source.Points,
                TodayPoints = source.TodayPoints,
                EarnedPoints = source.EarnedPoints,
                SpentPoints = source.SpentPoints,
                SignInStreak = source.SignInStreak,
                HasSignedInToday = source.HasSignedInToday,
                IsPermanentVip = source.IsPermanentVip,
                InviteCode = source.InviteCode,
                InvitedCount = source.InvitedCount,
                SuccessfulInviteCount = source.SuccessfulInviteCount,
                PendingCheckinInviteCount = source.PendingCheckinInviteCount,
                InvitePoints = source.InvitePoints,
                SignInDays = source.SignInDays.Select(day => new TaskCenterSignInDay
                {
                    Label = day.Label,
                    Points = day.Points,
                    IsChecked = day.IsChecked,
                    IsToday = day.IsToday,
                }).ToList(),
                DailyTasks = source.DailyTasks.Select(CloneTask).ToList(),
                OneTimeTasks = source.OneTimeTasks.Select(CloneTask).ToList(),
                LongTermTasks = source.LongTermTasks.Select(CloneTask).ToList(),
                Transactions = source.Transactions.Select(transaction => new PointTransaction
                {
                    Id = transaction.Id,
                    Title = transaction.Title,
                    TimeText = transaction.TimeText,
                    Amount = transaction.Amount,
                    Type = transaction.Type,
                }).ToList(),
                StoreItems = source.StoreItems.Select(item => new PointStoreItem
                {
                    Id = item.Id,
                    Category = item.Category,
                    Title = item.Title,
                    Description = item.Description,
                    Points = item.Points,
                    Price = item.Price,
                    DurationDays = item.DurationDays,
                    Stock = item.Stock,
                    ImageUrl = item.ImageUrl,
                    IconGlyph = item.IconGlyph,
                    AvailablePoints = source.Points,
                }).ToList(),
                ExchangeRecords = source.ExchangeRecords.Select(record => new ExchangeRecord
                {
                    Id = record.Id,
                    Title = record.Title,
                    Points = record.Points,
                    CreatedAt = record.CreatedAt,
                    StatusText = record.StatusText,
                }).ToList(),
                InviteRecords = source.InviteRecords.Select(record => new InviteRewardRecord
                {
                    Id = record.Id,
                    Title = record.Title,
                    Points = record.Points,
                    CreatedAt = record.CreatedAt,
                    StatusText = record.StatusText,
                }).ToList(),
            };
        }

        private static TaskCenterTaskItem CloneTask(TaskCenterTaskItem task)
            => new()
            {
                Id = task.Id,
                Title = task.Title,
                Description = task.Description,
                RewardPoints = task.RewardPoints,
                Current = task.Current,
                Target = task.Target,
                IsCompleted = task.IsCompleted,
                IsClaimed = task.IsClaimed,
                IsRepeatable = task.IsRepeatable,
                IconGlyph = task.IconGlyph,
            };
    }
}
