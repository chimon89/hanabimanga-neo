using Newtonsoft.Json;

namespace hanabimanga.Models
{
    public sealed class UsernameCheckResult
    {
        [JsonProperty("available")]
        public bool Available { get; set; }

        [JsonProperty("reason")]
        public string? Reason { get; set; }
    }
}
