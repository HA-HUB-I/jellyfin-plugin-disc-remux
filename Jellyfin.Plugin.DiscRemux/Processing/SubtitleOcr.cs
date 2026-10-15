using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscRemux.Processing;

/// <summary>A subtitle cue.</summary>
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// Converts a bitmap (DVD/VobSub) subtitle stream to SRT: ffmpeg renders the stream on a black canvas, keeps one frame per
/// change (mpdecimate) and logs its timestamp (showinfo); tesseract reads each frame.
/// </summary>
public sealed partial class SubtitleOcr
{
    // Capital letters that are words on their own (Latin A/I/O, Cyrillic В/С/К/И/А/О/У/Я).
    private const string OneLetterWords = "AIOВСКИАОУЯ";

    private readonly ILogger _logger;
    private readonly string _ffmpeg;
    private readonly string _tesseract;

    public SubtitleOcr(ILogger logger, string ffmpegPath, string tesseractPath)
    {
        _logger = logger;
        _ffmpeg = ffmpegPath;
        _tesseract = tesseractPath;
    }

    /// <summary>Returns the installed tesseract language models, or an empty set when tesseract is missing.</summary>
    public async Task<HashSet<string>> GetInstalledLanguagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(_tesseract, ["--list-langs"], cancellationToken).ConfigureAwait(false);
            return result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.Contains(' ', StringComparison.Ordinal))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    /// <summary>OCRs subtitle stream <paramref name="streamIndex"/> of <paramref name="subtitleFile"/> (an MKV holding only bitmap subtitles).</summary>
    public async Task<IReadOnlyList<SubtitleCue>> RecognizeAsync(
        string subtitleFile,
        int streamIndex,
        int width,
        int height,
        TimeSpan duration,
        string tesseractLanguage,
        string workDir,
        CancellationToken cancellationToken)
    {
        var frameDir = Path.Combine(workDir, "ocr-" + streamIndex.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(frameDir);

        string seconds = Math.Ceiling(duration.TotalSeconds + 30).ToString(CultureInfo.InvariantCulture);
        string graph = string.Create(
            CultureInfo.InvariantCulture,
            $"color=black:s={width}x{height}:r=10:d={seconds}[c];[c][0:s:{streamIndex}]overlay=eof_action=pass,mpdecimate=hi=64*12:lo=64*5:frac=0.1:max=0,showinfo,negate,scale=iw*2:ih*2:flags=lanczos[o]");
        var render = await ProcessRunner.RunAsync(
            _ffmpeg,
            ["-hide_banner", "-nostdin", "-nostats", "-v", "info", "-y", "-i", subtitleFile, "-filter_complex", graph, "-map", "[o]", "-fps_mode", "vfr", Path.Combine(frameDir, "f%05d.png")],
            cancellationToken).ConfigureAwait(false);
        if (render.ExitCode != 0)
        {
            throw new InvalidOperationException("ffmpeg subtitle render failed: " + Tail(render.StdErr));
        }

        var times = ShowInfoRegex().Matches(render.StdErr)
            .Select(m => TimeSpan.FromSeconds(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)))
            .ToList();
        times.Add(duration + TimeSpan.FromSeconds(30));

        var frames = Directory.GetFiles(frameDir, "f*.png").Order(StringComparer.Ordinal).ToList();
        var texts = new ConcurrentDictionary<int, string>();
        var env = new Dictionary<string, string> { ["OMP_THREAD_LIMIT"] = "1" };
        await Parallel.ForEachAsync(
            Enumerable.Range(0, frames.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount), CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                var ocr = await ProcessRunner.RunAsync(_tesseract, [frames[i], "stdout", "-l", tesseractLanguage, "--psm", "6"], ct, env).ConfigureAwait(false);
                texts[i] = Clean(ocr.StdOut);
            }).ConfigureAwait(false);

        var cues = new List<SubtitleCue>();
        for (int i = 0; i < frames.Count && i + 1 < times.Count; i++)
        {
            var text = texts.GetValueOrDefault(i, string.Empty);
            if (text.Length == 0)
            {
                continue;
            }

            if (cues.Count > 0 && cues[^1].Text == text && (times[i] - cues[^1].End).TotalSeconds < 0.15)
            {
                cues[^1] = cues[^1] with { End = times[i + 1] };
                continue;
            }

            cues.Add(new SubtitleCue(times[i], times[i + 1], text));
        }

        Directory.Delete(frameDir, recursive: true);
        _logger.LogInformation("Disc Remux: OCR ({Language}) produced {Count} cues from {Frames} frames", tesseractLanguage, cues.Count, frames.Count);
        return cues;
    }

    public static void WriteSrt(string path, IReadOnlyList<SubtitleCue> cues)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < cues.Count; i++)
        {
            sb.Append(i + 1).Append('\n')
              .Append(SrtTime(cues[i].Start)).Append(" --> ").Append(SrtTime(cues[i].End)).Append('\n')
              .Append(cues[i].Text).Append("\n\n");
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Fixes the usual tesseract artefacts on subtitle bitmaps.</summary>
    internal static string Clean(string raw)
    {
        var lines = new List<string>();
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.Trim()
                .Replace('|', 'I')
                .Replace('“', '"').Replace('”', '"').Replace('„', '"')
                .Replace('—', '-');
            // "П рекрасен" -> "Прекрасен", but keep real one-letter words ("В града", "I went", "A man").
            line = SplitCapitalRegex().Replace(line, m => OneLetterWords.Contains(m.Groups[2].Value, StringComparison.Ordinal) ? m.Value : m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value);
            line = TrailingNoiseRegex().Replace(line, string.Empty).Trim();

            // tesseract has no ♪ glyph: song lines come out as "tf Let me see", "f On Jupiter", "2 in other words".
            line = LeadingNoteRegex().Replace(line, "♪ ");
            line = TrailingNoteRegex().Replace(line, " ♪");
            if (line.Length > 0 && line.Any(char.IsLetterOrDigit))
            {
                lines.Add(line);
            }
        }

        return string.Join('\n', lines);
    }

    private static string SrtTime(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");

    private static string Tail(string s) => s.Length > 800 ? s[^800..] : s;

    [GeneratedRegex(@"\] n:\s*\d+ .*?pts_time:([0-9.]+)")]
    private static partial Regex ShowInfoRegex();

    // Only Cyrillic: tesseract often detaches the first (capital) letter there; in Latin scripts this rule would do more harm than good.
    [GeneratedRegex(@"(^|[\s\-""])((?=\p{Lu})\p{IsCyrillic}) ((?:(?=\p{Ll})\p{IsCyrillic}){2,})")]
    private static partial Regex SplitCapitalRegex();

    [GeneratedRegex(@"\s+[Il1]$")]
    private static partial Regex TrailingNoiseRegex();

    // Tokens that are never words on their own; "2" only before a lowercase word.
    [GeneratedRegex(@"^(?:(?:tf|ff|Jf|J\)|J|f|£|#|\$|Sf|af|ſ)\s+(?=\p{L})|2\s+(?=\p{Ll}))")]
    private static partial Regex LeadingNoteRegex();

    [GeneratedRegex(@"(?<=\p{L}[.,!?]?)\s+(?:tf|ff|Jf|J|f|£|#|\$|ſ)$")]
    private static partial Regex TrailingNoteRegex();
}
