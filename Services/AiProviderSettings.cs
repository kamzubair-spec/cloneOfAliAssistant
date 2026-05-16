using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace eZBERP_AI_IDE.Services;

public enum AiProvider
{
    DeepSeek,
    OpenAI
}

public static class AiProviderSettings
{
    private const string DefaultDeepSeekBaseUrl = "https://api.deepseek.com";
    private const string DefaultOpenAiBaseUrl = "https://api.openai.com/v1";

    public static AiProvider Provider
    {
        get
        {
            var raw = GetSetting("AI_PROVIDER", "NOT_SET");
            
            if (string.IsNullOrWhiteSpace(raw) || raw == "NOT_SET") return AiProvider.DeepSeek;

            // Extremely permissive: check for "open" anywhere in the value
            var normalized = raw.ToLowerInvariant();
            return normalized.Contains("openai") || normalized.Contains("open")
                ? AiProvider.OpenAI
                : AiProvider.DeepSeek;
        }
    }

    public static string ProviderDisplayName => Provider == AiProvider.OpenAI ? "OpenAI" : "DeepSeek";

    public static string ApiKey => Provider == AiProvider.OpenAI
        ? GetSetting("OPENAI_API_KEY", string.Empty)
        : GetSetting("DEEPSEEK_API_KEY", string.Empty);

    public static string ChatCompletionsUrl => Provider == AiProvider.OpenAI
        ? CombineUrl(GetSetting("OPENAI_BASE_URL", DefaultOpenAiBaseUrl), "chat/completions")
        : CombineUrl(GetSetting("DEEPSEEK_BASE_URL", DefaultDeepSeekBaseUrl), "v1/chat/completions");

    public static string OpenAiChatCompletionsUrl => CombineUrl(GetSetting("OPENAI_BASE_URL", DefaultOpenAiBaseUrl), "chat/completions");

    public static string DeepSeekBalanceUrl => CombineUrl(GetSetting("DEEPSEEK_BASE_URL", DefaultDeepSeekBaseUrl), "user/balance");

    public static TimeSpan RequestTimeout => GetTimeout();

    public static bool UseOpenAiForInlineImages => GetBooleanSetting("EZBERP_USE_OPENAI_FOR_INLINE_IMAGES", true);

    public static string OpenAiVisionModel => GetSetting("OPENAI_VISION_MODEL", GetSetting("OPENAI_CONFIG_MODEL", "gpt-4o"));

    public static string GetSetting(string name, string fallback)
    {
        // 1. Check current process (fastest, but can be stale)
        var processValue = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        if (!string.IsNullOrWhiteSpace(processValue)) return processValue.Trim();

        // 2. Check User level (reads from Registry on Windows - bypasses stale process blocks)
        var userValue = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(userValue)) return userValue.Trim();

        // 3. Check Machine level
        var machineValue = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        if (!string.IsNullOrWhiteSpace(machineValue)) return machineValue.Trim();
        
        return fallback;
    }

    public static string GetSettingSource(string name)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process))) return "Process";
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User))) return "User";
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine))) return "Machine";
        return "None/Fallback";
    }

    private static TimeSpan GetTimeout()
    {
        var providerSpecificName = Provider == AiProvider.OpenAI ? "OPENAI_TIMEOUT_SECONDS" : "DEEPSEEK_TIMEOUT_SECONDS";
        var configuredValue = GetSetting(providerSpecificName, GetSetting("EZBERP_AI_TIMEOUT_SECONDS", "300"));

        if (int.TryParse(configuredValue, out var timeoutSeconds) && timeoutSeconds > 0)
        {
            return TimeSpan.FromSeconds(timeoutSeconds);
        }

        return TimeSpan.FromMinutes(5);
    }

    private static bool GetBooleanSetting(string name, bool fallback)
    {
        var configuredValue = GetSetting(name, fallback ? "true" : "false");
        return configuredValue.Equals("true", StringComparison.OrdinalIgnoreCase)
            || configuredValue.Equals("1", StringComparison.OrdinalIgnoreCase)
            || configuredValue.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || configuredValue.Equals("y", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineUrl(string baseUrl, string relativePath)
    {
        return $"{baseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}";
    }

    private static string FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }
}
