using System.Collections;
using System.Globalization;
using System.Resources;
using System.Windows;

namespace ReadAloudStudio;

internal static class UiText
{
    public const string English = "en-GB";
    public const string Chinese = "zh-CN";
    private static readonly ResourceManager Strings = new("ReadAloudStudio.Strings", typeof(UiText).Assembly);
    private static readonly CultureInfo Zh = CultureInfo.GetCultureInfo(Chinese);
    private static CultureInfo culture = CultureInfo.GetCultureInfo(English);
    private static ResourceDictionary? activeDictionary;
    public static string LanguageCode => culture.Name;
    public static string For(string code, string key) => Lookup(CultureInfo.GetCultureInfo(code), key);
    public static string Tr(string key) => Lookup(culture, key);
    public static string Tr(string key, params object?[] values) => string.Format(culture, Tr(key), values);
    private static string Lookup(CultureInfo language, string key) =>
        Strings.GetString(key, language) ?? throw new MissingManifestResourceException(key);

    public static void SetLanguage(string? code)
    {
        var next = CultureInfo.GetCultureInfo(code == Chinese ? Chinese : English);
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (culture == next && activeDictionary != null && dictionaries.Contains(activeDictionary)) return;
        var text = new ResourceDictionary();
        foreach (DictionaryEntry item in Strings.GetResourceSet(next, true, true)!)
            text["Ui." + item.Key] = item.Value;
        culture = next;
        CultureInfo.CurrentUICulture = next;
        CultureInfo.DefaultThreadCurrentUICulture = next;
        int at = activeDictionary == null ? -1 : dictionaries.IndexOf(activeDictionary);
        if (at < 0) dictionaries.Add(text); else dictionaries[at] = text;
        activeDictionary = text;
    }

    // Stored reports and older service versions use Chinese diagnostic labels.
    // Translate only those labels, leaving transcripts, quoted words and technical details intact.
    private static readonly Lazy<Dictionary<string,string>> DiagnosticKeys = new(() =>
    {
        var keys = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Strings.GetResourceSet(Zh, true, true)!)
        {
            string key = (string)entry.Key;
            if (!key.StartsWith("Diag", StringComparison.Ordinal) && key is not ("MicOpenError" or "UnlocatedWord")) continue;
            keys[(string)entry.Value!] = key;
            keys[For(English,key)] = key;
        }
        return keys;
    });
    private static readonly string[] DiagnosticFormats = ["DiffDelete","DiffInsert","DiffReplace","ReasonSkipped","ReasonReviewCount"];
    private static readonly string[] ServerErrorPrefixes = ["DiagDemoFailed","DiagPrepareFailed","DiagAssessFailed","DiagRescoreFailed","DiagTimingModel"];

    public static string Diagnostic(string value)
    {
        if (DiagnosticKeys.Value.TryGetValue(value, out var key)) return Tr(key);
        foreach (string formatKey in DiagnosticFormats)
        {
            string pattern = Lookup(Zh, formatKey);
            int placeholder = pattern.IndexOf("{0}", StringComparison.Ordinal);
            string prefix = pattern[..placeholder], suffix = pattern[(placeholder+3)..];
            if (value.Length >= prefix.Length + suffix.Length && value.StartsWith(prefix,StringComparison.Ordinal)
                && value.EndsWith(suffix,StringComparison.Ordinal))
                return Tr(formatKey, value.Substring(prefix.Length,value.Length-prefix.Length-suffix.Length));
        }
        foreach (string prefixKey in ServerErrorPrefixes)
        {
            string prefix = Lookup(Zh,prefixKey);
            if (value.StartsWith(prefix,StringComparison.Ordinal))
                return Tr(prefixKey) + " " + Diagnostic(value[prefix.Length..].TrimStart());
        }
        int separator = value.IndexOf(": ",StringComparison.Ordinal);
        if (separator > 0 && (value[..separator].EndsWith("Error",StringComparison.Ordinal)
                          || value[..separator].EndsWith("Exception",StringComparison.Ordinal)))
            return value[..(separator+2)] + Diagnostic(value[(separator+2)..]);
        return value;
    }

    public static string Reasons(IEnumerable<string> reasons)
    {
        var lines = reasons.Where(s=>!string.IsNullOrWhiteSpace(s)).Select(Diagnostic);
        if (LanguageCode == Chinese) return string.Join("。",lines.Select(s=>s.TrimEnd('。')));
        return string.Join(" ",lines.Select(s=>char.IsPunctuation(s[^1]) ? s : s + "."));
    }
}
