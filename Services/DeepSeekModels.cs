namespace eZBERP_AI_IDE.Services;

public static class DeepSeekModels
{
    private const string DefaultDeepSeekFlashModel = "deepseek-v4-flash";
    private const string DefaultDeepSeekProModel = "deepseek-v4-pro";
    private const string DefaultOpenAiBasicModel = "gpt-4o-mini";
    private const string DefaultOpenAiAdvancedModel = "gpt-4o";

    public static string V4Flash => GetDeepSeekModel("DEEPSEEK_FLASH_MODEL", DefaultDeepSeekFlashModel);
    public static string V4Pro => GetDeepSeekModel("DEEPSEEK_PRO_MODEL", DefaultDeepSeekProModel);

    public static string Normal => AiProviderSettings.Provider == AiProvider.OpenAI
        ? GetOpenAiModel("OPENAI_BASIC_MODEL", DefaultOpenAiBasicModel)
        : GetDeepSeekModel("DEEPSEEK_NORMAL_MODEL", V4Flash);

    public static string Complex => AiProviderSettings.Provider == AiProvider.OpenAI
        ? GetOpenAiModel("OPENAI_ADVANCED_MODEL", DefaultOpenAiAdvancedModel)
        : GetDeepSeekModel("DEEPSEEK_COMPLEX_MODEL", V4Pro);

    public static string Config => AiProviderSettings.Provider == AiProvider.OpenAI
        ? GetOpenAiModel("OPENAI_CONFIG_MODEL", Complex)
        : GetDeepSeekModel("DEEPSEEK_CONFIG_MODEL", Complex);

    public static string Vision => AiProviderSettings.OpenAiVisionModel;

    public static string Coding => AiProviderSettings.Provider == AiProvider.OpenAI
        ? GetOpenAiModel("OPENAI_CODING_MODEL", Complex)
        : GetDeepSeekModel("DEEPSEEK_CODING_MODEL", Complex);

    private static string GetDeepSeekModel(string environmentVariableName, string fallback)
    {
        return AiProviderSettings.GetSetting(environmentVariableName, fallback);
    }

    private static string GetOpenAiModel(string environmentVariableName, string fallback)
    {
        return AiProviderSettings.GetSetting(environmentVariableName, fallback);
    }
}
