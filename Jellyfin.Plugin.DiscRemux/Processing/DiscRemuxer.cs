using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DiscRemux.Configuration;
using Jellyfin.Plugin.DiscRemux.Dvd;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscRemux.Processing;

/// <summary>Outcome of one disc.</summary>
public sealed record RemuxResult(bool Changed, string Message);

/// <summary>Turns one <see cref="DiscCandidate"/> into "&lt;Movie&gt;.mkv" plus "&lt;Movie&gt;.&lt;lang&gt;.srt" files.</summary>
public sealed partial class DiscRemuxer
{
    private readonly ILogger _logger;
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly string _tempRoot;

    public DiscRemuxer(ILogger logger, string ffmpegPath, string ffprobePath, string tempRoot)
    {
        _logger = logger;
        _ffmpeg = ffmpegPath;
        _ffprobe = ffprobePath;
        _tempRoot = tempRoot;
    }

    public async Task<RemuxResult> ProcessAsync(DiscCandidate disc, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var title = PickMainTitle(disc.DiscFolder);
        if (title is null)
        {
            return new RemuxResult(false, $"no readable title set in {disc.DiscFolder}");
        }

        var (info, vobs) = title.Value;
        var vobDuration = await SumDurationsAsync(vobs, cancellationToken).ConfigureAwait(false);
        double mismatch = Math.Abs(vobDuration.TotalSeconds - info.Duration.TotalSeconds) / Math.Max(1, info.Duration.TotalSeconds);
        string summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{disc.OutputBaseName}: VTS_{info.TitleSet:00} x{vobs.Count} VOB, title {info.Duration:h\\:mm\\:ss}, VOBs {vobDuration:h\\:mm\\:ss}, audio [{string.Join(", ", info.Audio.Select(a => $"{a.Language ?? "und"} {a.Codec} {a.Channels}ch"))}], subs [{string.Join(", ", info.Subtitles.Select(s => s.Language ?? "und"))}]");
        _logger.LogInformation("Disc Remux: {Summary}", summary);

        if (info.HasAngleBlocks || mismatch > config.MaxDurationMismatch)
        {
            return new RemuxResult(false, $"skipped {disc.OutputBaseName}: multi-angle or seamless-branching disc (VOB length differs {mismatch:P0} from the title), join it manually");
        }

        string output = Path.Combine(disc.MovieFolder, disc.OutputBaseName + ".mkv");
        if (File.Exists(output))
        {
            return new RemuxResult(false, $"skipped {disc.OutputBaseName}: {output} already exists");
        }

        if (config.DryRun)
        {
            return new RemuxResult(false, "dry run: would create " + output + " and replace " + string.Join(", ", disc.Replaces));
        }

        string concat = "concat:" + string.Join('|', vobs);
        var probe = await ProbeAsync(concat, info.PaletteOption, cancellationToken).ConfigureAwait(false);
        var audio = info.Audio.Where(a => probe.StreamIds.Contains(a.StreamId)).ToList();
        var subs = info.Subtitles.Where(s => probe.StreamIds.Contains(s.StreamId)).ToList();
        if (audio.Count == 0)
        {
            return new RemuxResult(false, $"skipped {disc.OutputBaseName}: none of the IFO audio streams were found in the VOBs");
        }

        var workDir = Path.Combine(_tempRoot, "disc-remux", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var srtFiles = new List<string>();
            var converted = new HashSet<int>();
            if (config.OcrSubtitles && subs.Count > 0)
            {
                await OcrAsync(disc, config, info, concat, probe, subs, workDir, srtFiles, converted, cancellationToken).ConfigureAwait(false);
            }

            var keptSubs = subs.Where(s => !(config.DropConvertedBitmapSubtitles && converted.Contains(s.StreamId))).ToList();
            string part = Path.Combine(disc.MovieFolder, "." + disc.OutputBaseName + ".disc-remux.part");
            var args = BuildMuxArguments(concat, info, audio, keptSubs, config, disc.OutputBaseName, part);
            var mux = await ProcessRunner.RunAsync(_ffmpeg, args, cancellationToken).ConfigureAwait(false);
            if (mux.ExitCode != 0)
            {
                File.Delete(part);
                srtFiles.ForEach(File.Delete);
                return new RemuxResult(false, $"ffmpeg failed for {disc.OutputBaseName}: {Tail(mux.StdErr)}");
            }

            File.Move(part, output);
            MoveToTrash(disc, config);
            return new RemuxResult(true, $"created {output} ({srtFiles.Count} SRT)");
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    private async Task OcrAsync(
        DiscCandidate disc,
        PluginConfiguration config,
        DvdTitleInfo info,
        string concat,
        ProbeInfo probe,
        List<DvdSubtitleStream> subs,
        string workDir,
        List<string> srtFiles,
        HashSet<int> converted,
        CancellationToken cancellationToken)
    {
        var ocr = new SubtitleOcr(_logger, _ffmpeg, config.TesseractPath);
        var installed = await ocr.GetInstalledLanguagesAsync(cancellationToken).ConfigureAwait(false);
        if (installed.Count == 0)
        {
            _logger.LogWarning("Disc Remux: tesseract not found ({Path}); bitmap subtitles are kept as they are", config.TesseractPath);
            return;
        }

        var wanted = config.OcrLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var todo = subs
            .Select(s => (Stream: s, Model: Languages.ToTesseract(s.Language)))
            .Where(x => x.Model is not null && installed.Contains(x.Model) && (wanted.Count == 0 || wanted.Contains(x.Stream.Language!)))
            .ToList();
        if (todo.Count == 0)
        {
            return;
        }

        // One cheap pass that copies only the subtitle streams; same timeline as the final MKV.
        string subsOnly = Path.Combine(workDir, "subs.mkv");
        var extractArgs = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-v", "error", "-y", "-fflags", "+genpts", "-analyzeduration", "200M", "-probesize", "200M", "-palette", info.PaletteOption, "-i", concat };
        foreach (var (stream, _) in todo)
        {
            extractArgs.AddRange(["-map", Id(stream.StreamId)]);
        }

        extractArgs.AddRange(["-c:s", "dvdsub", "-palette", info.PaletteOption, "-f", "matroska", subsOnly]);
        var extract = await ProcessRunner.RunAsync(_ffmpeg, extractArgs, cancellationToken).ConfigureAwait(false);
        if (extract.ExitCode != 0)
        {
            _logger.LogWarning("Disc Remux: subtitle extraction failed, skipping OCR: {Error}", Tail(extract.StdErr));
            return;
        }

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool defaultAssigned = false;
        for (int i = 0; i < todo.Count; i++)
        {
            var (stream, model) = todo[i];
            var cues = await ocr.RecognizeAsync(subsOnly, i, probe.Width, probe.Height, info.Duration, model!, workDir, cancellationToken).ConfigureAwait(false);
            if (cues.Count < 10)
            {
                _logger.LogWarning("Disc Remux: OCR of {Language} stream 0x{Id:x} gave only {Count} cues; keeping the bitmap track", stream.Language, stream.StreamId, cues.Count);
                continue;
            }

            string suffix = stream.Language!;
            if (stream.IsForced)
            {
                suffix += ".forced";
            }
            else if (!defaultAssigned && string.Equals(stream.Language, config.DefaultSubtitleLanguage, StringComparison.OrdinalIgnoreCase))
            {
                suffix += ".default";
                defaultAssigned = true;
            }

            string name = suffix;
            for (int n = 2; !usedNames.Add(name); n++)
            {
                name = suffix + "." + n.ToString(CultureInfo.InvariantCulture);
            }

            string srt = Path.Combine(disc.MovieFolder, disc.OutputBaseName + "." + name + ".srt");
            SubtitleOcr.WriteSrt(srt, cues);
            srtFiles.Add(srt);
            converted.Add(stream.StreamId);
        }
    }

    internal static List<string> BuildMuxArguments(
        string concat,
        DvdTitleInfo info,
        IReadOnlyList<DvdAudioStream> audio,
        IReadOnlyList<DvdSubtitleStream> subs,
        PluginConfiguration config,
        string title,
        string output)
    {
        var a = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-v", "error", "-y", "-fflags", "+genpts", "-analyzeduration", "200M", "-probesize", "200M", "-palette", info.PaletteOption, "-i", concat, "-map", "0:v:0" };
        int outAudio = 0;
        var meta = new List<string>();
        if (config.AddStereoAac)
        {
            a.AddRange(["-map", Id(audio[0].StreamId)]);
            meta.AddRange(["-c:a:0", "aac", "-b:a:0", config.AacBitrate.ToString(CultureInfo.InvariantCulture) + "k", "-ac:a:0", "2",
                "-metadata:s:a:0", "language=" + Languages.ToIso6392(audio[0].Language), "-metadata:s:a:0", "title=Stereo (AAC)", "-disposition:a:0", "default"]);
            outAudio++;
        }

        foreach (var s in audio)
        {
            a.AddRange(["-map", Id(s.StreamId)]);
            string idx = outAudio.ToString(CultureInfo.InvariantCulture);
            meta.AddRange(["-c:a:" + idx, "copy", "-metadata:s:a:" + idx, "language=" + Languages.ToIso6392(s.Language),
                "-metadata:s:a:" + idx, "title=" + AudioTitle(s), "-disposition:a:" + idx, outAudio == 0 ? "default" : "0"]);
            outAudio++;
        }

        for (int i = 0; i < subs.Count; i++)
        {
            a.AddRange(["-map", Id(subs[i].StreamId)]);
            string idx = i.ToString(CultureInfo.InvariantCulture);
            meta.AddRange(["-metadata:s:s:" + idx, "language=" + Languages.ToIso6392(subs[i].Language), "-disposition:s:" + idx, subs[i].IsForced ? "forced" : "0"]);
        }

        a.AddRange(["-c:v", "copy", "-c:s", "dvdsub", "-palette", info.PaletteOption]);
        a.AddRange(meta);
        a.AddRange(["-disposition:v:0", "default", "-metadata", "title=" + title, "-f", "matroska", output]);
        return a;
    }

    private (DvdTitleInfo Info, List<string> Vobs)? PickMainTitle(string folder)
    {
        (DvdTitleInfo Info, List<string> Vobs)? best = null;
        foreach (var ifo in Directory.GetFiles(folder))
        {
            var m = IfoNameRegex().Match(Path.GetFileName(ifo));
            if (!m.Success)
            {
                continue;
            }

            int vts = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            DvdTitleInfo info;
            try
            {
                info = IfoParser.Parse(ifo, vts);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentOutOfRangeException or OverflowException)
            {
                _logger.LogWarning(ex, "Disc Remux: cannot read {Ifo}", ifo);
                continue;
            }

            var vobs = Directory.GetFiles(folder)
                .Select(f => (Path: f, Match: VobNameRegex().Match(Path.GetFileName(f))))
                .Where(x => x.Match.Success && int.Parse(x.Match.Groups[1].Value, CultureInfo.InvariantCulture) == vts)
                .OrderBy(x => int.Parse(x.Match.Groups[2].Value, CultureInfo.InvariantCulture))
                .Select(x => x.Path)
                .ToList();
            if (vobs.Count > 0 && (best is null || info.Duration > best.Value.Info.Duration))
            {
                best = (info, vobs);
            }
        }

        return best;
    }

    private async Task<TimeSpan> SumDurationsAsync(IEnumerable<string> files, CancellationToken cancellationToken)
    {
        double total = 0;
        foreach (var file in files)
        {
            var r = await ProcessRunner.RunAsync(_ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file], cancellationToken).ConfigureAwait(false);
            if (double.TryParse(r.StdOut.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                total += seconds;
            }
        }

        return TimeSpan.FromSeconds(total);
    }

    private async Task<ProbeInfo> ProbeAsync(string concat, string palette, CancellationToken cancellationToken)
    {
        var r = await ProcessRunner.RunAsync(
            _ffprobe,
            ["-v", "error", "-analyzeduration", "200M", "-probesize", "200M", "-palette", palette, "-show_entries", "stream=id,codec_type,width,height", "-of", "json", concat],
            cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(r.StdOut);
        var ids = new HashSet<int>();
        int width = 720, height = 480;
        foreach (var s in json.RootElement.GetProperty("streams").EnumerateArray())
        {
            if (s.TryGetProperty("id", out var id) && id.GetString() is { } hex && hex.StartsWith("0x", StringComparison.Ordinal))
            {
                ids.Add(int.Parse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }

            if (s.GetProperty("codec_type").GetString() == "video" && s.TryGetProperty("width", out var w) && s.TryGetProperty("height", out var h))
            {
                width = w.GetInt32();
                height = h.GetInt32();
            }
        }

        return new ProbeInfo(ids, width, height);
    }

    private void MoveToTrash(DiscCandidate disc, PluginConfiguration config)
    {
        string trash = string.IsNullOrWhiteSpace(config.TrashPath)
            ? Path.Combine(disc.MovieFolder, DiscLocator.HiddenOriginalFolder)
            : Path.Combine(config.TrashPath, disc.OutputBaseName);
        Directory.CreateDirectory(trash);
        foreach (var path in disc.Replaces)
        {
            string target = Path.Combine(trash, Path.GetFileName(path));
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Move(path, target);
                }
                else if (File.Exists(path))
                {
                    File.Move(path, target, overwrite: true);
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Disc Remux: could not move {Path} to {Trash}; remove it by hand", path, trash);
            }
        }
    }

    private static string AudioTitle(DvdAudioStream s)
    {
        string codec = s.Codec switch { "ac3" => "Dolby Digital", "dts" => "DTS", "pcm" => "PCM", "mp2" => "MPEG", _ => s.Codec };
        string layout = s.Channels switch { 6 => "5.1", 2 => "2.0", 1 => "1.0", _ => s.Channels.ToString(CultureInfo.InvariantCulture) + "ch" };
        string extra = s.CodeExtension is 3 or 4 ? " (commentary)" : string.Empty;
        return codec + " " + layout + extra;
    }

    private static string Id(int streamId) => "0:i:0x" + streamId.ToString("x", CultureInfo.InvariantCulture);

    private static string Tail(string s) => s.Length > 800 ? s[^800..] : s;

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [GeneratedRegex(@"^VTS_(\d\d)_0\.IFO$", RegexOptions.IgnoreCase)]
    private static partial Regex IfoNameRegex();

    [GeneratedRegex(@"^VTS_(\d\d)_([1-9])\.VOB$", RegexOptions.IgnoreCase)]
    private static partial Regex VobNameRegex();

    private sealed record ProbeInfo(HashSet<int> StreamIds, int Width, int Height);
}
