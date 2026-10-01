using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ussd.Gateway.Menus;

/// <summary>Localised screen text (en, sn, nd) with {placeholder} substitution. English is the fallback.</summary>
public sealed partial class Texts
{
    public static readonly string[] Languages = ["en", "sn", "nd"];
    public const int MaxScreenLength = 182;

    private readonly Dictionary<string, Dictionary<string, string>> _byLanguage;

    public Texts(Dictionary<string, Dictionary<string, string>> byLanguage) => _byLanguage = byLanguage;

    public static Texts Load(string directory) =>
        new(Languages.ToDictionary(l => l, l => JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(directory, $"{l}.json")))!));

    public IReadOnlyDictionary<string, string> Table(string language) => _byLanguage[language];

    public bool Has(string language, string key) => _byLanguage.TryGetValue(language, out var t) && t.ContainsKey(key);

    public string Get(string language, string key, IReadOnlyDictionary<string, string>? vars = null)
    {
        if (!_byLanguage.TryGetValue(language, out var table) || !table.TryGetValue(key, out var template))
        {
            template = _byLanguage["en"].TryGetValue(key, out var en) ? en : key;
        }
        return vars is null ? template : Fill(template, vars);
    }

    public static string Fill(string template, IReadOnlyDictionary<string, string> vars) =>
        Placeholder().Replace(template, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    [GeneratedRegex(@"\{([a-zA-Z0-9_.]+)\}")]
    private static partial Regex Placeholder();

    // GSM 03.38 basic character set (+ the extension table characters we allow). USSD screens must stay in it.
    private const string Gsm7 = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
    private const string Gsm7Extension = "^{}\\[~]|€";

    /// <summary>Length in GSM-7 septets (extension characters count double).</summary>
    public static int Gsm7Length(string text) => text.Sum(c => Gsm7Extension.Contains(c) ? 2 : 1);

    public static bool IsGsm7(string text) => text.All(c => Gsm7.Contains(c) || Gsm7Extension.Contains(c));

    /// <summary>Replaces characters outside GSM-7 (e.g. curly quotes, en dashes from data) so a screen never breaks.</summary>
    public static string ToGsm7(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                '‘' or '’' => '\'',
                '“' or '”' => '"',
                '–' or '—' => '-',
                '…' => '.',
                _ when IsGsm7(c.ToString()) => c,
                _ => '?',
            });
        }
        return sb.ToString();
    }
}
