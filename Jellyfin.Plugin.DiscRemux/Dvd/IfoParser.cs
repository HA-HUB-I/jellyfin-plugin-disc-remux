using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.DiscRemux.Dvd;

/// <summary>Audio stream of a DVD title set, with the MPEG-PS stream id ffmpeg reports for it.</summary>
public sealed record DvdAudioStream(int Index, int StreamId, string Codec, string? Language, int Channels, int CodeExtension);

/// <summary>Subpicture (bitmap subtitle) stream of a DVD title set.</summary>
public sealed record DvdSubtitleStream(int Index, int StreamId, string? Language, int CodeExtension)
{
    /// <summary>Gets a value indicating whether the stream is a forced-subtitles stream.</summary>
    public bool IsForced => CodeExtension == 9;
}

/// <summary>What one VTS_xx_0.IFO says about its longest program chain.</summary>
public sealed class DvdTitleInfo
{
    public required int TitleSet { get; init; }

    public required TimeSpan Duration { get; init; }

    public required bool IsWidescreen { get; init; }

    public required bool IsPal { get; init; }

    public required bool HasAngleBlocks { get; init; }

    public required IReadOnlyList<DvdAudioStream> Audio { get; init; }

    public required IReadOnlyList<DvdSubtitleStream> Subtitles { get; init; }

    /// <summary>Gets the 16 entry subpicture palette as rrggbb hex strings.</summary>
    public required IReadOnlyList<string> Palette { get; init; }

    /// <summary>Gets the palette in the form ffmpeg's dvdsub decoder/encoder "palette" option expects.</summary>
    public string PaletteOption => string.Join(',', Palette);
}

/// <summary>Minimal parser for DVD-Video VTS information files (VTS_xx_0.IFO).</summary>
public static class IfoParser
{
    private const int SectorSize = 2048;

    public static DvdTitleInfo Parse(string ifoPath, int titleSet)
    {
        return Parse(File.ReadAllBytes(ifoPath), titleSet);
    }

    public static DvdTitleInfo Parse(byte[] d, int titleSet)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (d.Length < 0x400 || Encoding.ASCII.GetString(d, 0, 12) != "DVDVIDEO-VTS")
        {
            throw new InvalidDataException("Not a DVD VTS IFO file");
        }

        byte videoAttr = d[0x200];
        bool isPal = ((videoAttr >> 4) & 0x3) == 1;
        bool isWide = ((videoAttr >> 2) & 0x3) == 3;

        int pgcit = checked((int)U32(d, 0xCC) * SectorSize);
        int pgcCount = U16(d, pgcit);
        int bestPgc = -1;
        TimeSpan bestDuration = TimeSpan.Zero;
        for (int i = 0; i < pgcCount; i++)
        {
            int pgc = pgcit + (int)U32(d, pgcit + 8 + (i * 8) + 4);
            if (pgc + 0xEC > d.Length)
            {
                continue;
            }

            var duration = DecodeTime(d, pgc + 4);
            if (duration > bestDuration)
            {
                bestDuration = duration;
                bestPgc = pgc;
            }
        }

        if (bestPgc < 0)
        {
            throw new InvalidDataException("IFO has no program chains");
        }

        return new DvdTitleInfo
        {
            TitleSet = titleSet,
            Duration = bestDuration,
            IsWidescreen = isWide,
            IsPal = isPal,
            HasAngleBlocks = HasAngles(d, bestPgc),
            Audio = ReadAudio(d, bestPgc),
            Subtitles = ReadSubtitles(d, bestPgc, isWide),
            Palette = ReadPalette(d, bestPgc),
        };
    }

    private static List<DvdAudioStream> ReadAudio(byte[] d, int pgc)
    {
        var list = new List<DvdAudioStream>();
        int count = Math.Min((int)U16(d, 0x202), 8);
        for (int i = 0; i < count; i++)
        {
            ushort control = U16(d, pgc + 0x0C + (i * 2));
            if ((control & 0x8000) == 0)
            {
                continue;
            }

            int number = (control >> 8) & 0x07;
            int a = 0x204 + (i * 8);
            int coding = (d[a] >> 5) & 0x07;
            (string codec, int baseId) = coding switch
            {
                0 => ("ac3", 0x80),
                2 or 3 => ("mp2", 0x1C0),
                4 => ("pcm", 0xA0),
                6 => ("dts", 0x88),
                _ => ("unknown", 0x80),
            };
            list.Add(new DvdAudioStream(i, baseId + number, codec, ReadLanguage(d, a, (d[a] >> 2) & 0x3), (d[a + 1] & 0x07) + 1, d[a + 5]));
        }

        return list;
    }

    private static List<DvdSubtitleStream> ReadSubtitles(byte[] d, int pgc, bool isWide)
    {
        var list = new List<DvdSubtitleStream>();
        int count = Math.Min((int)U16(d, 0x254), 32);
        for (int i = 0; i < count; i++)
        {
            uint control = U32(d, pgc + 0x1C + (i * 4));
            if ((control & 0x80000000) == 0)
            {
                continue;
            }

            int number = (int)(isWide ? (control >> 16) : (control >> 24)) & 0x1F;
            int a = 0x256 + (i * 6);
            list.Add(new DvdSubtitleStream(i, 0x20 + number, ReadLanguage(d, a, d[a] & 0x3), d[a + 5]));
        }

        return list;
    }

    private static string[] ReadPalette(byte[] d, int pgc)
    {
        var palette = new string[16];
        for (int k = 0; k < 16; k++)
        {
            int o = pgc + 0xA4 + (k * 4);
            double y = d[o + 1], cr = d[o + 2], cb = d[o + 3];
            int r = Clamp(y + (1.402 * (cr - 128)));
            int g = Clamp(y - (0.344136 * (cb - 128)) - (0.714136 * (cr - 128)));
            int b = Clamp(y + (1.772 * (cb - 128)));
            palette[k] = string.Create(CultureInfo.InvariantCulture, $"{r:x2}{g:x2}{b:x2}");
        }

        return palette;
    }

    private static bool HasAngles(byte[] d, int pgc)
    {
        int cells = d[pgc + 3];
        int table = U16(d, pgc + 0xE8);
        if (table == 0)
        {
            return false;
        }

        for (int c = 0; c < cells; c++)
        {
            int o = pgc + table + (c * 24);
            if (o >= d.Length)
            {
                break;
            }

            // Cell category bits 5-4: block type, 1 = angle block.
            if (((d[o] >> 4) & 0x3) == 1)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadLanguage(byte[] d, int offset, int languageType)
    {
        if (languageType != 1)
        {
            return null;
        }

        char c1 = (char)d[offset + 2], c2 = (char)d[offset + 3];
        return char.IsAsciiLetter(c1) && char.IsAsciiLetter(c2)
            ? string.Concat(char.ToLowerInvariant(c1), char.ToLowerInvariant(c2))
            : null;
    }

    private static TimeSpan DecodeTime(byte[] d, int o)
    {
        static int Bcd(byte b) => ((b >> 4) * 10) + (b & 0x0F);
        double fps = ((d[o + 3] >> 6) & 0x3) == 1 ? 25.0 : 29.97;
        return new TimeSpan(Bcd(d[o]), Bcd(d[o + 1]), Bcd(d[o + 2])) + TimeSpan.FromSeconds(Bcd((byte)(d[o + 3] & 0x3F)) / fps);
    }

    private static int Clamp(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);

    private static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o, 2));

    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o, 4));
}
