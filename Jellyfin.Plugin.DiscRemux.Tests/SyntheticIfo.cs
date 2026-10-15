using System;
using System.Buffers.Binary;
using System.Text;

namespace Jellyfin.Plugin.DiscRemux.Tests;

/// <summary>Builds a minimal VTS IFO in memory so tests do not need disc images.</summary>
internal static class SyntheticIfo
{
    public static byte[] Build(
        bool wide = true,
        bool pal = false,
        (int Coding, string Lang, int Channels, int Number)[]? audio = null,
        (string Lang, int Number4x3, int NumberWide, int CodeExt)[]? subs = null,
        (int H, int M, int S)? mainDuration = null,
        bool angleCell = false)
    {
        audio ??= [(0, "en", 6, 0)];
        subs ??= [("en", 0, 0, 1), ("bg", 1, 1, 1)];
        var (h, m, s) = mainDuration ?? (1, 46, 20);

        var d = new byte[3 * 2048];
        Encoding.ASCII.GetBytes("DVDVIDEO-VTS").CopyTo(d, 0);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(0xCC), 1); // PGCITI at sector 1
        d[0x200] = (byte)(((pal ? 1 : 0) << 4) | ((wide ? 3 : 0) << 2));

        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(0x202), (ushort)audio.Length);
        for (int i = 0; i < audio.Length; i++)
        {
            int a = 0x204 + (i * 8);
            d[a] = (byte)((audio[i].Coding << 5) | (1 << 2));
            d[a + 1] = (byte)(audio[i].Channels - 1);
            d[a + 2] = (byte)audio[i].Lang[0];
            d[a + 3] = (byte)audio[i].Lang[1];
            d[a + 5] = 1;
        }

        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(0x254), (ushort)subs.Length);
        for (int i = 0; i < subs.Length; i++)
        {
            int a = 0x256 + (i * 6);
            d[a] = 1;
            d[a + 2] = (byte)subs[i].Lang[0];
            d[a + 3] = (byte)subs[i].Lang[1];
            d[a + 5] = (byte)subs[i].CodeExt;
        }

        // PGCITI with two PGCs: a 2 minute extra and the main title.
        int pgcit = 2048;
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(pgcit), 2);
        WritePgc(d, pgcit, 0, 0x100, (0, 2, 0), audio, subs, false);
        WritePgc(d, pgcit, 1, 0x300, (h, m, s), audio, subs, angleCell);
        return d;
    }

    private static void WritePgc(
        byte[] d,
        int pgcit,
        int entry,
        int offset,
        (int H, int M, int S) time,
        (int Coding, string Lang, int Channels, int Number)[] audio,
        (string Lang, int Number4x3, int NumberWide, int CodeExt)[] subs,
        bool angleCell)
    {
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(pgcit + 8 + (entry * 8) + 4), (uint)offset);
        int p = pgcit + offset;
        d[p + 3] = 1; // one cell
        d[p + 4] = Bcd(time.H);
        d[p + 5] = Bcd(time.M);
        d[p + 6] = Bcd(time.S);
        d[p + 7] = 0xC0; // 29.97 fps, 0 frames
        for (int i = 0; i < audio.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(p + 0x0C + (i * 2)), (ushort)(0x8000 | (audio[i].Number << 8)));
        }

        for (int i = 0; i < subs.Length; i++)
        {
            uint v = 0x80000000 | ((uint)subs[i].Number4x3 << 24) | ((uint)subs[i].NumberWide << 16);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(p + 0x1C + (i * 4)), v);
        }

        // Palette entry 0: white (Y=235, Cr=Cb=128); entry 1: black (Y=16).
        d[p + 0xA4 + 1] = 235;
        d[p + 0xA4 + 2] = 128;
        d[p + 0xA4 + 3] = 128;
        d[p + 0xA8 + 1] = 16;
        d[p + 0xA8 + 2] = 128;
        d[p + 0xA8 + 3] = 128;

        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(p + 0xE8), 0xEC);
        d[p + 0xEC] = (byte)(angleCell ? 0x50 : 0x00);
    }

    private static byte Bcd(int v) => (byte)(((v / 10) << 4) | (v % 10));
}
