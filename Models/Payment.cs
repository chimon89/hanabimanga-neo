using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace hanabimanga.Models
{
    public sealed class PaymentOrder
    {
        public string Id { get; set; } = "";
        public string TradeNo { get; set; } = "";
        public string? HypayTradeNo { get; set; }
        public decimal Amount { get; set; }
        public string Status { get; set; } = "pending";
        public string? PayUrl { get; set; }
        public DateTime? PaidAt { get; set; }
        public JObject? ProductSnapshot { get; set; }
        public bool Synced { get; set; }

        public bool IsPaid => string.Equals(Status, "paid", StringComparison.OrdinalIgnoreCase);
        public bool IsPending => string.Equals(Status, "pending", StringComparison.OrdinalIgnoreCase);
        public string AmountText => $"¥{Amount:0.##}";
        public string StatusText => Status.ToLowerInvariant() switch
        {
            "paid" => "已支付",
            "failed" => "下单失败",
            "refunded" => "已退款",
            _ => "待支付",
        };
    }

    internal sealed class RawCreateOrderResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("data")]
        public RawPaymentOrderData? Data { get; set; }

        [JsonProperty("error")]
        public string? Error { get; set; }

        [JsonProperty("message")]
        public string? Message { get; set; }
    }

    internal sealed class RawQueryOrderResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("data")]
        public RawPaymentOrderData? Data { get; set; }

        [JsonProperty("error")]
        public string? Error { get; set; }

        [JsonProperty("message")]
        public string? Message { get; set; }
    }

    internal sealed class RawPaymentOrderData
    {
        [JsonProperty("id")]
        public string? Id { get; set; }

        [JsonProperty("order_id")]
        public string? OrderId { get; set; }

        [JsonProperty("trade_no")]
        public string? TradeNo { get; set; }

        [JsonProperty("hypay_trade_no")]
        public string? HypayTradeNo { get; set; }

        [JsonProperty("amount")]
        public decimal Amount { get; set; }

        [JsonProperty("status")]
        public string? Status { get; set; }

        [JsonProperty("pay_url")]
        public string? PayUrl { get; set; }

        [JsonProperty("pay_info")]
        public string? PayInfo { get; set; }

        [JsonProperty("paid_at")]
        public DateTime? PaidAt { get; set; }

        [JsonProperty("product_snapshot")]
        public JObject? ProductSnapshot { get; set; }

        [JsonProperty("synced")]
        public bool? Synced { get; set; }
    }
}
