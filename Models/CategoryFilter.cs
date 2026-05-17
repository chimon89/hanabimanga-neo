using System.Collections.Generic;

namespace hanabimanga.Models
{
    /// <summary>类型筛选项，Id 为 null 表示「全部」。</summary>
    public sealed class CategoryOption
    {
        public long? Id { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>排名（排序）筛选项。</summary>
    public sealed class SortOption
    {
        public string Name { get; set; } = "";
        public string OrderColumn { get; set; } = "updated_at";

        public static IReadOnlyList<SortOption> All { get; } = new[]
        {
            new SortOption { Name = "默认", OrderColumn = "updated_at" },
            new SortOption { Name = "评分最高", OrderColumn = "rating_average" },
            new SortOption { Name = "最新上架", OrderColumn = "created_at" },
        };
    }

    /// <summary>漫画风格筛选项，Filter 为 region 列的 PostgREST 条件片段，null 表示「全部」。</summary>
    public sealed class RegionOption
    {
        public string Name { get; set; } = "";
        public string? Filter { get; set; }

        public static IReadOnlyList<RegionOption> All { get; } = new[]
        {
            new RegionOption { Name = "全部", Filter = null },
            new RegionOption { Name = "日漫", Filter = "eq.jp" },
            new RegionOption { Name = "韩漫", Filter = "eq.kr" },
            new RegionOption { Name = "美漫", Filter = "eq.us" },
            new RegionOption { Name = "其他", Filter = "not.in.(jp,kr,us)" },
        };
    }

    /// <summary>连载状态筛选项，IsFinished 为 null 表示「全部状态」。</summary>
    public sealed class StatusOption
    {
        public string Name { get; set; } = "";
        public bool? IsFinished { get; set; }

        public static IReadOnlyList<StatusOption> All { get; } = new[]
        {
            new StatusOption { Name = "全部状态", IsFinished = null },
            new StatusOption { Name = "连载中", IsFinished = false },
            new StatusOption { Name = "已完结", IsFinished = true },
        };
    }

    internal sealed class RawCategoryOption
    {
        [Newtonsoft.Json.JsonProperty("id")]
        public long Id { get; set; }

        [Newtonsoft.Json.JsonProperty("name")]
        public string? Name { get; set; }
    }
}
