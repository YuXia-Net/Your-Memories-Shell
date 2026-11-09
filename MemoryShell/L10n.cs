using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml.Markup;

namespace MemoryShell;

internal static class L10n
{
    internal sealed record Language(string Code, string NativeName);
    internal static readonly Language[] Languages =
    [
        new("zh-CN", "简体中文"), new("zh-TW", "繁體中文"), new("en", "English"), new("ja", "日本語"),
        new("ko", "한국어"), new("de", "Deutsch"), new("fr", "Français"), new("es", "Español")
    ];
    private static readonly Lazy<Dictionary<string, string>> Source = new(() => Read("zh-CN"));
    private static readonly Lazy<Dictionary<string, string>> English = new(() => Read("en"));
    private static Dictionary<string, string> current = new(StringComparer.Ordinal);
    private static string SettingsPath => Path.Combine(WorkspaceStore.CustomFolder, "Language.json");
    public static bool HasPreference => File.Exists(SettingsPath);
    public static string Code { get; private set; } = "zh-CN";
    public static string Preference { get; private set; } = "system";

    public static void Initialize()
    {
        try
        {
            if (File.Exists(SettingsPath) && new FileInfo(SettingsPath).Length <= 1024)
                Preference = JsonSerializer.Deserialize<string>(File.ReadAllText(SettingsPath)) ?? "system";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        if (Preference != "system" && !Languages.Any(item => item.Code == Preference)) Preference = "system";
        ApplyCode(Preference == "system" ? Match(CultureInfo.CurrentUICulture.Name) : Preference);
    }

    public static void ApplyCode(string code)
    {
        if (!Languages.Any(item => item.Code == code)) throw new ArgumentOutOfRangeException(nameof(code));
        Code = code;
        current = Code == "zh-CN" ? Source.Value : Code == "en" ? English.Value : Read(Code);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(Code);
        // Change UI culture only.
        // Keep the system PRI language for native controls.
    }

    internal static string Match(string culture)
    {
        if (culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return culture.Contains("TW", StringComparison.OrdinalIgnoreCase) || culture.Contains("HK", StringComparison.OrdinalIgnoreCase) ||
                culture.Contains("MO", StringComparison.OrdinalIgnoreCase) || culture.Contains("Hant", StringComparison.OrdinalIgnoreCase) ? "zh-TW" : "zh-CN";
        var language = culture.Split('-')[0];
        return Languages.Any(item => item.Code == language) ? language : "en";
    }

    public static void SavePreference(string value)
    {
        if (value != "system" && !Languages.Any(item => item.Code == value)) throw new ArgumentOutOfRangeException(nameof(value));
        Directory.CreateDirectory(WorkspaceStore.CustomFolder);
        var temporary = SettingsPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, SettingsPath, true);
            Preference = value;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Get(string key) => current.GetValueOrDefault(key) ?? English.Value.GetValueOrDefault(key) ?? Source.Value.GetValueOrDefault(key) ?? key;
    public static string Format(string key, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    private static Dictionary<string, string> Read(string code)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"MemoryShell.Localization.{code}.json");
        return stream is null ? new(StringComparer.Ordinal) : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new(StringComparer.Ordinal);
    }
}

[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class LocalizeExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    protected override object ProvideValue() => L10n.Get(Key);
}
