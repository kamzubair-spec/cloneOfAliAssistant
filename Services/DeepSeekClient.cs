using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace eZBERP_AI_IDE.Services;

public sealed class DeepSeekClient
{
    private readonly HttpClient _httpClient;
    private readonly AiProvider _provider;
    private readonly string _apiKey;

    public DeepSeekClient(string? apiKey = null)
    {
        _provider = AiProviderSettings.Provider;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? AiProviderSettings.ApiKey : apiKey.Trim();
        _httpClient = new HttpClient
        {
            Timeout = AiProviderSettings.RequestTimeout
        };
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }
    }

    public async Task<BalanceResult> GetBalanceAsync()
    {
        if (_provider == AiProvider.OpenAI)
        {
            return new BalanceResult(string.Empty, false, false);
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return new BalanceResult($"{AiProviderSettings.ProviderDisplayName} API key is not configured", true, false);
        }

        return await GetDeepSeekBalanceAsync();
    }

    public async Task<string> SendChatAsync(string model, string systemPrompt, string userPrompt, double temperature, int maxTokens)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return $"API Error: {AiProviderSettings.ProviderDisplayName} API key is not configured.";
        }

        var request = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature,
            max_tokens = maxTokens,
            stream = false
        };

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(AiProviderSettings.ChatCompletionsUrl, content);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return $"API Error ({AiProviderSettings.ProviderDisplayName}): {responseJson}";
            }

            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;
        }
        catch (TaskCanceledException)
        {
            return $"API Error: {AiProviderSettings.ProviderDisplayName} request timed out after {AiProviderSettings.RequestTimeout.TotalSeconds:0} seconds.";
        }
        catch (Exception ex)
        {
            return $"API Error ({AiProviderSettings.ProviderDisplayName}): {ex.Message}";
        }
    }

    public async Task<string> SendVisionChatAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<AiChatContentPart> userContent,
        double temperature,
        int maxTokens)
    {
        var openAiApiKey = AiProviderSettings.GetSetting("OPENAI_API_KEY", string.Empty);
        if (string.IsNullOrWhiteSpace(openAiApiKey))
        {
            return "API Error: OpenAI API key is not configured. Set OPENAI_API_KEY to analyze Jira stories with inline images.";
        }

        var contentParts = new List<object>();
        foreach (var part in userContent)
        {
            if (part.Kind == AiChatContentKind.Image)
            {
                if (string.IsNullOrWhiteSpace(part.DataUrl))
                {
                    continue;
                }

                contentParts.Add(new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = part.DataUrl
                    }
                });
            }
            else if (!string.IsNullOrWhiteSpace(part.Text))
            {
                contentParts.Add(new
                {
                    type = "text",
                    text = part.Text
                });
            }
        }

        var request = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = contentParts }
            },
            temperature,
            max_tokens = maxTokens,
            stream = false
        };

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, AiProviderSettings.OpenAiChatCompletionsUrl)
            {
                Content = content
            };
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", openAiApiKey);

            using var response = await _httpClient.SendAsync(httpRequest);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return $"API Error (OpenAI vision): {responseJson}";
            }

            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;
        }
        catch (TaskCanceledException)
        {
            return $"API Error: OpenAI vision request timed out after {AiProviderSettings.RequestTimeout.TotalSeconds:0} seconds.";
        }
        catch (Exception ex)
        {
            return $"API Error (OpenAI vision): {ex.Message}";
        }
    }

    private async Task<BalanceResult> GetDeepSeekBalanceAsync()
    {
        try
        {
            using var response = await _httpClient.GetAsync(AiProviderSettings.DeepSeekBalanceUrl);
            if (!response.IsSuccessStatusCode)
            {
                return response.StatusCode == HttpStatusCode.Unauthorized
                    ? new BalanceResult("DeepSeek: Invalid API Key", true, false)
                    : new BalanceResult($"DeepSeek Balance: API Error ({response.StatusCode})", false, false);
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("balance_infos", out var balanceInfos) && balanceInfos.GetArrayLength() > 0)
            {
                var balanceInfo = balanceInfos[0];
                var totalBalance = balanceInfo.GetProperty("total_balance").GetString() ?? "0";
                var currency = balanceInfo.GetProperty("currency").GetString() ?? "USD";
                return new BalanceResult($"DeepSeek Balance: {currency} {totalBalance}", false, false);
            }

            return new BalanceResult("DeepSeek Balance: Unable to parse", false, false);
        }
        catch
        {
            return new BalanceResult("DeepSeek Balance: Unavailable", false, false);
        }
    }
}

public sealed record BalanceResult(string Text, bool IsError, bool IsWarning);

public enum AiChatContentKind
{
    Text,
    Image
}

public sealed record AiChatContentPart(
    AiChatContentKind Kind,
    string Text = "",
    string FileName = "",
    string MimeType = "",
    string DataUrl = "");
