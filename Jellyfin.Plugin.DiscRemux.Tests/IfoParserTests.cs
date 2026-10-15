using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.DiscRemux.Dvd;
using Xunit;

namespace Jellyfin.Plugin.DiscRemux.Tests;

public class IfoParserTests
{
    [Fact]
    public void PicksLongestProgramChain()
    {
        var info = IfoParser.Parse(SyntheticIfo.Build(), 3);
        Assert.Equal(new TimeSpan(1, 46, 20), info.Duration);
        Assert.Equal(3, info.TitleSet);
        Assert.True(info.IsWidescreen);
        Assert.False(info.IsPal);
        Assert.False(info.HasAngleBlocks);
    }

    [Fact]
    public void MapsAudioToMpegPsStreamIds()
    {
        var info = IfoParser.Parse(SyntheticIfo.Build(audio: [(0, "en", 6, 0), (6, "de", 6, 1), (2, "fr", 2, 0)]), 1);
        Assert.Collection(
            info.Audio,
            a => Assert.Equal((0x80, "ac3", "en", 6), (a.StreamId, a.Codec, a.Language, a.Channels)),
            a => Assert.Equal((0x89, "dts", "de", 6), (a.StreamId, a.Codec, a.Language, a.Channels)),
            a => Assert.Equal((0x1C0, "mp2", "fr", 2), (a.StreamId, a.Codec, a.Language, a.Channels)));
    }

    [Fact]
    public void UsesWideSubpictureNumberOnWidescreen()
    {
        var subs = new[] { ("en", 0, 2, 1), ("bg", 1, 3, 9) };
        var wide = IfoParser.Parse(SyntheticIfo.Build(wide: true, subs: subs), 1);
        var narrow = IfoParser.Parse(SyntheticIfo.Build(wide: false, subs: subs), 1);

        Assert.Equal(new[] { 0x22, 0x23 }, wide.Subtitles.Select(s => s.StreamId));
        Assert.Equal(new[] { 0x20, 0x21 }, narrow.Subtitles.Select(s => s.StreamId));
        Assert.True(wide.Subtitles[1].IsForced);
        Assert.Equal("bg", wide.Subtitles[1].Language);
    }

    [Fact]
    public void ConvertsPaletteFromYCrCb()
    {
        var info = IfoParser.Parse(SyntheticIfo.Build(), 1);
        Assert.Equal("ebebeb", info.Palette[0]);
        Assert.Equal("101010", info.Palette[1]);
        Assert.Equal(16, info.Palette.Count);
    }

    [Fact]
    public void DetectsAngleBlocks()
    {
        Assert.True(IfoParser.Parse(SyntheticIfo.Build(angleCell: true), 1).HasAngleBlocks);
    }

    [Fact]
    public void RejectsNonIfo()
    {
        Assert.Throws<InvalidDataException>(() => IfoParser.Parse(new byte[4096], 1));
    }

    /// <summary>Optional check against a real disc: set DISC_REMUX_SAMPLE_IFO to a VTS_xx_0.IFO path.</summary>
    [Fact]
    public void RealIfoIfAvailable()
    {
        var path = Environment.GetEnvironmentVariable("DISC_REMUX_SAMPLE_IFO");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        var info = IfoParser.Parse(path, 3);
        Console.WriteLine($"duration={info.Duration} wide={info.IsWidescreen} pal={info.IsPal} angles={info.HasAngleBlocks}");
        Console.WriteLine("audio: " + string.Join(", ", info.Audio.Select(a => $"0x{a.StreamId:x} {a.Codec} {a.Language} {a.Channels}ch")));
        Console.WriteLine("subs: " + string.Join(", ", info.Subtitles.Select(s => $"0x{s.StreamId:x} {s.Language} ext{s.CodeExtension}")));
        Console.WriteLine("palette: " + info.PaletteOption);
        Assert.True(info.Duration > TimeSpan.FromMinutes(10));
    }
}
