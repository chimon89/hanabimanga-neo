using System;
using Newtonsoft.Json;

namespace hanabimanga.Models
{
    /// <summary>
    /// `request-email-verification` Edge Function 的成功响应。
    /// MaskedEmail 形如 "s***d@icloud.com",用于在弹窗里告知用户验证码寄到了哪个邮箱;
    /// ExpiresAt 是 UTC 过期时间戳,UI 可据此渲染"还有 X 分钟"的倒计时。
    /// </summary>
    public sealed record EmailVerificationDispatchResult(string? MaskedEmail, DateTime? ExpiresAt);

    /// <summary>
    /// RPC `confirm_email_verification` 的响应。
    /// 字段语义参考后端 Kotlin 模型 ConfirmEmailVerificationResponse:
    /// - <see cref="Verified"/>:本次调用是否成功把邮箱标为已验证(全新通过)。
    /// - <see cref="AlreadyVerified"/>:邮箱在本次调用前就已经是已验证态(幂等场景)。
    /// - <see cref="Message"/>:服务端给的本地化提示(失败/已验证场景常见)。
    /// - <see cref="Reward"/>:积分发放结果。仅在 Verified=true(首次验证)时才会有意义,
    ///   already_verified=true 路径下 reward 通常为 null 或 success=false。
    /// </summary>
    public sealed class ConfirmEmailVerificationResponse
    {
        [JsonProperty("verified")]
        public bool Verified { get; set; }

        [JsonProperty("already_verified")]
        public bool AlreadyVerified { get; set; }

        [JsonProperty("message")]
        public string? Message { get; set; }

        [JsonProperty("reward")]
        public EmailVerificationReward? Reward { get; set; }

        public bool IsVerified => Verified || AlreadyVerified;
    }

    /// <summary>
    /// confirm_email_verification 返回的积分发放结果。
    /// task_id 对应任务中心里"验证邮箱"任务的定义,balance_after 是积分到账后的余额。
    /// </summary>
    public sealed class EmailVerificationReward
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("task_id")]
        public string? TaskId { get; set; }

        [JsonProperty("points_awarded")]
        public int PointsAwarded { get; set; }

        [JsonProperty("balance_after")]
        public long BalanceAfter { get; set; }

        [JsonProperty("message")]
        public string? Message { get; set; }

        [JsonProperty("error")]
        public string? Error { get; set; }
    }
}
