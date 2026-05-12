using System;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace hanabimanga.Models
{
    [Table("profiles")]
    public class UserProfile : BaseModel
    {
        [PrimaryKey("id")]
        public string Id { get; set; } = "";

        [Column("username")]
        public string? Username { get; set; }

        [Column("display_name")]
        public string? DisplayName { get; set; }

        [Column("avatar_url")]
        public string? AvatarUrl { get; set; }

        [Column("vip_expiration_date")]
        public DateTime? VipExpirationDate { get; set; }
    }
}
