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

        [Column("banner_url")]
        public string? BannerUrl { get; set; }

        [Column("vip_expiration_date")]
        public DateTime? VipExpirationDate { get; set; }

        [Column("invite_code")]
        public string? InviteCode { get; set; }

        [Column("email_verified_at")]
        public DateTime? EmailVerifiedAt { get; set; }

        [Column("created_at")]
        public DateTime? CreatedAt { get; set; }

        public bool IsEmailVerified => EmailVerifiedAt.HasValue;

        public bool HasActiveVip => VipExpirationDate is { } expiresAt && expiresAt > DateTime.UtcNow;

        public bool IsPermanentVip =>
            VipExpirationDate is { } expiresAt && expiresAt.Date >= new DateTime(2050, 12, 31);
    }
}
