using System.Windows;

namespace VideoConverterUI.Services;

public static class LocalizationService
{
    private const string EnglishDictionary = "pack://application:,,,/VideoConverterUI;component/Assets/Strings.en.xaml";

    /// <summary>
    /// Swaps the language resource dictionary. English always stays loaded underneath as the
    /// fallback for keys a translation does not define.
    /// </summary>
    public static void ApplyLanguage(string lang)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;

        foreach (var old in dicts.Where(d => d.Contains("LangCode")).ToList())
            dicts.Remove(old);

        if (!dicts.Any(d => d.Source != null && d.Source.OriginalString.Contains("Strings.en.xaml")))
            dicts.Insert(0, new ResourceDictionary { Source = new Uri(EnglishDictionary) });

        if (lang == "en") return;

        try
        {
            dicts.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/VideoConverterUI;component/Assets/Strings.{lang}.xaml")
            });
        }
        catch { /* unknown language – English fallback stays active */ }
    }
}
