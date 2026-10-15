using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.DiscRemux.Processing;

/// <summary>Maps the ISO 639-1 codes stored on DVDs to ISO 639-2 (Matroska) and tesseract model names.</summary>
public static class Languages
{
    private static readonly Dictionary<string, string> TesseractOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "chi_sim",
        ["sr"] = "srp",
        ["no"] = "nor",
        ["nb"] = "nor",
    };

    /// <summary>Returns the ISO 639-2 code ffmpeg writes into the Matroska language tag, or "und".</summary>
    public static string ToIso6392(string? twoLetter)
    {
        var culture = TryCulture(twoLetter);
        return culture?.ThreeLetterISOLanguageName ?? "und";
    }

    /// <summary>Returns the tesseract language model for a two letter code, or null.</summary>
    public static string? ToTesseract(string? twoLetter)
    {
        if (string.IsNullOrEmpty(twoLetter))
        {
            return null;
        }

        return TesseractOverrides.TryGetValue(twoLetter, out var model) ? model : TryCulture(twoLetter)?.ThreeLetterISOLanguageName;
    }

    private static CultureInfo? TryCulture(string? twoLetter)
    {
        if (string.IsNullOrEmpty(twoLetter) || twoLetter.Length != 2)
        {
            return null;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(twoLetter);
            return culture.ThreeLetterISOLanguageName.Length == 3 && culture.ThreeLetterISOLanguageName != "ivl" ? culture : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
