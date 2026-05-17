using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using hanabimanga.Models;
using Newtonsoft.Json;

namespace hanabimanga.Services
{
    // Bangumi 公开 API 客户端（求书时搜索书籍条目）。
    // 接口文档站为 https://bangumi.github.io/api/ ，实际接口主机为 api.bgm.tv。
    public sealed class BangumiService
    {
        private static readonly Lazy<BangumiService> _instance = new(() => new BangumiService());
        public static BangumiService Instance => _instance.Value;

        private readonly HttpClient _httpClient;

        private BangumiService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "hanabimanga/1.0 (WinUI client)");
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }

        // type=1 表示书籍/漫画。无结果时 Bangumi 返回 404 或缺失 list 字段,均按空列表处理。
        public async Task<List<BangumiBook>> SearchBooksAsync(string keyword)
        {
            var trimmed = keyword?.Trim() ?? "";
            if (trimmed.Length == 0) return new List<BangumiBook>();

            var url =
                $"https://api.bgm.tv/search/subject/{Uri.EscapeDataString(trimmed)}" +
                "?type=1&responseGroup=medium&max_results=20";

            using var response = await _httpClient.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            Debug.WriteLine($"[bangumi] GET {url}: {(int)response.StatusCode}");

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new List<BangumiBook>();
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Bangumi 搜索失败:{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return new List<BangumiBook>();
            }

            RawBangumiSearchResponse? raw;
            try
            {
                raw = JsonConvert.DeserializeObject<RawBangumiSearchResponse>(body);
            }
            catch (JsonException)
            {
                // 无结果时部分情况下返回 {"code":404,...} 之类的非预期结构
                return new List<BangumiBook>();
            }

            if (raw?.List == null)
            {
                return new List<BangumiBook>();
            }

            return raw.List
                .Where(item => item.Id > 0)
                .Select(item => new BangumiBook
                {
                    Id = item.Id,
                    Name = item.Name ?? "",
                    NameCn = string.IsNullOrWhiteSpace(item.NameCn) ? null : item.NameCn,
                    CoverUrl = item.Images?.Common
                        ?? item.Images?.Medium
                        ?? item.Images?.Grid,
                })
                .ToList();
        }

        private sealed class RawBangumiSearchResponse
        {
            [JsonProperty("results")]
            public int Results { get; set; }

            [JsonProperty("list")]
            public List<RawBangumiSubject>? List { get; set; }
        }

        private sealed class RawBangumiSubject
        {
            [JsonProperty("id")]
            public long Id { get; set; }

            [JsonProperty("name")]
            public string? Name { get; set; }

            [JsonProperty("name_cn")]
            public string? NameCn { get; set; }

            [JsonProperty("images")]
            public RawBangumiImages? Images { get; set; }
        }

        private sealed class RawBangumiImages
        {
            [JsonProperty("common")]
            public string? Common { get; set; }

            [JsonProperty("medium")]
            public string? Medium { get; set; }

            [JsonProperty("grid")]
            public string? Grid { get; set; }
        }
    }
}
