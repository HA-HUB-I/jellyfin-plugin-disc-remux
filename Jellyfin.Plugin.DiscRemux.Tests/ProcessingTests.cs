using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.DiscRemux.Configuration;
using Jellyfin.Plugin.DiscRemux.Dvd;
using Jellyfin.Plugin.DiscRemux.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DiscRemux.Tests;

public class ProcessingTests
{
    [Theory]
    [InlineData("П рекрасен глас, капитане.", "Прекрасен глас, капитане.")]
    [InlineData("В града е тихо.", "В града е тихо.")]
    [InlineData("И трите ли?", "И трите ли?")]
    [InlineData("I went home", "I went home")]
    [InlineData("P rague is far", "P rague is far")]
    [InlineData("С него ли?", "С него ли?")]
    [InlineData("Д обре.", "Добре.")]
    [InlineData("- Да. I", "- Да.")]
    [InlineData("„Здравей“ — каза", "\"Здравей\" - каза")]
    [InlineData("   \n.\n", "")]
    public void CleansOcrText(string raw, string expected)
    {
        Assert.Equal(expected, SubtitleOcr.Clean(raw));
    }

    [Theory]
    [InlineData("bg", "bul", "bul")]
    [InlineData("en", "eng", "eng")]
    [InlineData("de", "deu", "deu")]
    [InlineData("zh", "zho", "chi_sim")]
    [InlineData(null, "und", null)]
    public void MapsLanguages(string? code, string iso6392, string? tesseract)
    {
        Assert.Equal(iso6392, Languages.ToIso6392(code));
        Assert.Equal(tesseract, Languages.ToTesseract(code));
    }

    [Fact]
    public void MuxArgumentsPutStereoAacFirstAndDefault()
    {
        var info = IfoParser.Parse(SyntheticIfo.Build(audio: [(0, "en", 6, 0), (0, "bg", 2, 1)]), 1);
        var args = DiscRemuxer.BuildMuxArguments("concat:a|b", info, info.Audio, info.Subtitles.Take(1).ToList(), new PluginConfiguration(), "Movie (2026)", "out.part");
        var line = string.Join(' ', args);

        Assert.Contains("-map 0:v:0 -map 0:i:0x80 -map 0:i:0x80 -map 0:i:0x81 -map 0:i:0x20", line, StringComparison.Ordinal);
        Assert.Contains("-c:a:0 aac -b:a:0 192k -ac:a:0 2", line, StringComparison.Ordinal);
        Assert.Contains("-disposition:a:0 default", line, StringComparison.Ordinal);
        Assert.Contains("-c:a:1 copy", line, StringComparison.Ordinal);
        Assert.Contains("-disposition:a:1 0", line, StringComparison.Ordinal);
        Assert.Contains("-metadata:s:a:2 language=bul", line, StringComparison.Ordinal);
        Assert.Contains("-disposition:s:0 0", line, StringComparison.Ordinal);
        Assert.Equal("out.part", args[^1]);
        Assert.Equal("matroska", args[^2]);
    }

    [Fact]
    public void MuxArgumentsWithoutAacKeepFirstTrackDefault()
    {
        var info = IfoParser.Parse(SyntheticIfo.Build(), 1);
        var args = DiscRemuxer.BuildMuxArguments("concat:a", info, info.Audio, [], new PluginConfiguration { AddStereoAac = false }, "M", "o");
        var line = string.Join(' ', args);
        Assert.DoesNotContain("aac", line, StringComparison.Ordinal);
        Assert.Contains("-c:a:0 copy", line, StringComparison.Ordinal);
        Assert.Contains("-disposition:a:0 default", line, StringComparison.Ordinal);
    }

    [Fact]
    public void LocatorFindsLibraryDiscAndImportedFragment()
    {
        var root = Directory.CreateTempSubdirectory("disc-remux-test-").FullName;
        try
        {
            var lib = Directory.CreateDirectory(Path.Combine(root, "lib")).FullName;
            var downloads = Directory.CreateDirectory(Path.Combine(root, "downloads")).FullName;

            // 1) A full VIDEO_TS ripped straight into the library.
            var videoTs = Directory.CreateDirectory(Path.Combine(lib, "Ripped (2001)", "VIDEO_TS")).FullName;
            File.WriteAllBytes(Path.Combine(videoTs, "VTS_01_0.IFO"), SyntheticIfo.Build());
            File.WriteAllBytes(Path.Combine(videoTs, "VTS_01_1.VOB"), new byte[10]);

            // 2) Radarr imported one VOB; the full disc is still in downloads.
            var release = Directory.CreateDirectory(Path.Combine(downloads, "Deep.Water.2026.NTSC.DVD5", "VIDEO_TS")).FullName;
            File.WriteAllBytes(Path.Combine(release, "VTS_03_0.IFO"), SyntheticIfo.Build());
            File.WriteAllBytes(Path.Combine(release, "VTS_03_1.VOB"), new byte[11]);
            File.WriteAllBytes(Path.Combine(release, "VTS_03_2.VOB"), new byte[12]);
            var movie = Directory.CreateDirectory(Path.Combine(lib, "Deep Water (2026)")).FullName;
            File.WriteAllBytes(Path.Combine(movie, "VTS_03_2.VOB"), new byte[12]);
            File.WriteAllText(Path.Combine(movie, "VTS_03_2.bg.srt"), "x");
            File.WriteAllText(Path.Combine(movie, "movie.nfo"), "x");

            // 3) A fragment with no source: only a warning.
            var orphan = Directory.CreateDirectory(Path.Combine(lib, "Orphan (2025)")).FullName;
            File.WriteAllBytes(Path.Combine(orphan, "VTS_08_6.VOB"), new byte[13]);

            // 4) Already processed originals are ignored.
            var hidden = Directory.CreateDirectory(Path.Combine(lib, "Done (2020)", DiscLocator.HiddenOriginalFolder, "VIDEO_TS")).FullName;
            File.WriteAllBytes(Path.Combine(hidden, "VTS_01_0.IFO"), SyntheticIfo.Build());

            var found = new DiscLocator(NullLogger.Instance).Find([lib], [downloads]).OrderBy(c => c.OutputBaseName, StringComparer.Ordinal).ToList();

            Assert.Equal(2, found.Count);
            var fragment = found[0];
            Assert.Equal(DiscCandidateKind.ImportedFragment, fragment.Kind);
            Assert.Equal("Deep Water (2026)", fragment.OutputBaseName);
            Assert.Equal(release, fragment.DiscFolder);
            Assert.Equal(new[] { "VTS_03_2.VOB", "VTS_03_2.bg.srt" }, fragment.Replaces.Select(Path.GetFileName).Order(StringComparer.Ordinal));

            var disc = found[1];
            Assert.Equal(DiscCandidateKind.LibraryDisc, disc.Kind);
            Assert.Equal("Ripped (2001)", disc.OutputBaseName);
            Assert.Equal(new[] { videoTs }, disc.Replaces);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
