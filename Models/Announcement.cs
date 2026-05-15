using System;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace hanabimanga.Models
{
    // 对应 public.announcements 表,字段定义见
    // hanabimanga-develop-skill reference/database.md 941 行起。
    [Table("announcements")]
    public class Announcement : BaseModel
    {
        [PrimaryKey("id")]
        public string Id { get; set; } = "";

        [Column("title")]
        public string Title { get; set; } = "";

        [Column("content")]
        public string? Content { get; set; }

        [Column("detail_content")]
        public string? DetailContent { get; set; }

        [Column("announcement_type")]
        public string AnnouncementType { get; set; } = "info";

        [Column("action_text")]
        public string? ActionText { get; set; }

        [Column("action_url")]
        public string? ActionUrl { get; set; }

        [Column("priority")]
        public int Priority { get; set; }

        [Column("is_active")]
        public bool IsActive { get; set; } = true;

        [Column("is_dismissible")]
        public bool IsDismissible { get; set; } = true;

        [Column("is_popup")]
        public bool IsPopup { get; set; }

        [Column("start_at")]
        public DateTime? StartAt { get; set; }

        [Column("end_at")]
        public DateTime? EndAt { get; set; }

        [Column("created_at")]
        public DateTime? CreatedAt { get; set; }

        [Column("updated_at")]
        public DateTime? UpdatedAt { get; set; }
    }
}
