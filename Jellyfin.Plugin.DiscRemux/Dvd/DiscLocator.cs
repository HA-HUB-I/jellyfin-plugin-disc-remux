using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscRemux.Dvd;

/// <summary>How a disc was found.</summary>
public enum DiscCandidateKind
{
    /// <summary>A complete VIDEO_TS structure sits inside a library folder.</summary>
    LibraryDisc,

    /// <summary>A single VTS_xx_x.VOB was imported into the library; the full disc was found in a source path.</summary>
    ImportedFragment,
}

/// <summary>A DVD that should become one MKV.</summary>
public sealed class DiscCandidate
{
    public required DiscCandidateKind Kind { get; init; }

    /// <summary>Gets the folder with the VTS_xx_0.IFO and VOB files.</summary>
    public required string DiscFolder { get; init; }

    /// <summary>Gets the library folder that receives the MKV.</summary>
    public required string MovieFolder { get; init; }

    /// <summary>Gets library files or folders that the MKV replaces (moved to the trash afterwards).</summary>
    public required IReadOnlyList<string> Replaces { get; init; }

    public string OutputBaseName => Path.GetFileName(MovieFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}

/// <summary>Finds DVD folder rips and split VOB imports.</summary>
public sealed partial class DiscLocator
{
    public const string HiddenOriginalFolder = ".disc-remux-original";

    private const int MaxSourceDepth = 6;

    private readonly ILogger _logger;

    public DiscLocator(ILogger logger)
    {
        _logger = logger;
    }

    public static bool IsTitleVob(string fileName) => TitleVobRegex().IsMatch(fileName);

    public static bool IsDiscFolder(string folder) => SafeFiles(folder).Any(f => TitleIfoRegex().IsMatch(Path.GetFileName(f)));

    /// <summary>Scans library roots for disc rips and, using the source paths, for imported fragments.</summary>
    public IReadOnlyList<DiscCandidate> Find(IEnumerable<string> libraryRoots, IReadOnlyCollection<string> sourceRoots)
    {
        var roots = libraryRoots.Select(Normalize).Distinct(StringComparer.Ordinal).ToList();
        var result = new List<DiscCandidate>();
        Dictionary<(string Name, long Size), List<string>>? sourceIndex = null;

        foreach (var root in roots)
        {
            foreach (var dir in WalkLibrary(root))
            {
                var candidate = Inspect(dir, roots, sourceRoots, ref sourceIndex);
                if (candidate is not null)
                {
                    result.Add(candidate);
                }
            }
        }

        return result;
    }

    /// <summary>Inspects one library folder (used for the "item added" fast path).</summary>
    public DiscCandidate? InspectFolder(string folder, IEnumerable<string> libraryRoots, IReadOnlyCollection<string> sourceRoots)
    {
        Dictionary<(string Name, long Size), List<string>>? sourceIndex = null;
        return Inspect(Normalize(folder), libraryRoots.Select(Normalize).ToList(), sourceRoots, ref sourceIndex);
    }

    private DiscCandidate? Inspect(
        string dir,
        IReadOnlyList<string> roots,
        IReadOnlyCollection<string> sourceRoots,
        ref Dictionary<(string Name, long Size), List<string>>? sourceIndex)
    {
        var files = SafeFiles(dir).ToList();
        bool isVideoTs = string.Equals(Path.GetFileName(dir), "VIDEO_TS", StringComparison.OrdinalIgnoreCase);
        string movieFolder = isVideoTs ? Path.GetDirectoryName(dir)! : dir;
        if (roots.Contains(movieFolder, StringComparer.Ordinal))
        {
            // Never write into the library root itself: there is no movie folder to name the MKV after.
            return null;
        }

        if (files.Any(f => TitleIfoRegex().IsMatch(Path.GetFileName(f))))
        {
            var replaces = new List<string>();
            if (isVideoTs)
            {
                replaces.Add(dir);
                var audioTs = Path.Combine(movieFolder, "AUDIO_TS");
                if (Directory.Exists(audioTs))
                {
                    replaces.Add(audioTs);
                }
            }
            else
            {
                replaces.AddRange(files.Where(f => DiscFileRegex().IsMatch(Path.GetFileName(f))));
            }

            return new DiscCandidate { Kind = DiscCandidateKind.LibraryDisc, DiscFolder = dir, MovieFolder = movieFolder, Replaces = replaces };
        }

        var fragments = files.Where(f => IsTitleVob(Path.GetFileName(f))).ToList();
        if (fragments.Count == 0)
        {
            return null;
        }

        sourceIndex ??= BuildSourceIndex(sourceRoots);
        foreach (var fragment in fragments)
        {
            var info = new FileInfo(fragment);
            if (!sourceIndex.TryGetValue((info.Name.ToUpperInvariant(), info.Length), out var matches))
            {
                continue;
            }

            var source = matches.FirstOrDefault(IsDiscFolder);
            if (source is null)
            {
                continue;
            }

            // The fragment plus sidecars named after it (VTS_03_2.nfo, VTS_03_2.bg.srt, ...).
            var stem = Path.GetFileNameWithoutExtension(fragment);
            var replaces = files
                .Where(f => Path.GetFileName(f).StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return new DiscCandidate { Kind = DiscCandidateKind.ImportedFragment, DiscFolder = source, MovieFolder = movieFolder, Replaces = replaces };
        }

        _logger.LogWarning("Disc Remux: {Folder} holds only a DVD fragment ({Files}) and no complete disc was found in the source paths", dir, string.Join(", ", fragments.Select(Path.GetFileName)));
        return null;
    }

    private Dictionary<(string Name, long Size), List<string>> BuildSourceIndex(IEnumerable<string> sourceRoots)
    {
        var index = new Dictionary<(string Name, long Size), List<string>>();
        foreach (var root in sourceRoots.Where(Directory.Exists))
        {
            foreach (var dir in Walk(root, MaxSourceDepth, skipHidden: false))
            {
                foreach (var file in SafeFiles(dir).Where(f => IsTitleVob(Path.GetFileName(f))))
                {
                    var key = (Path.GetFileName(file).ToUpperInvariant(), new FileInfo(file).Length);
                    if (!index.TryGetValue(key, out var list))
                    {
                        index[key] = list = [];
                    }

                    list.Add(dir);
                }
            }
        }

        _logger.LogDebug("Disc Remux: indexed {Count} title VOBs in source paths", index.Count);
        return index;
    }

    private static IEnumerable<string> WalkLibrary(string root) => Walk(root, int.MaxValue, skipHidden: true);

    private static IEnumerable<string> Walk(string root, int maxDepth, bool skipHidden)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            yield return dir;
            if (depth >= maxDepth)
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if ((skipHidden && name.StartsWith('.')) || string.Equals(name, HiddenOriginalFolder, StringComparison.Ordinal))
                {
                    continue;
                }

                stack.Push((child, depth + 1));
            }
        }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try
        {
            return Directory.GetFiles(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    [GeneratedRegex(@"^VTS_\d\d_[1-9]\.VOB$", RegexOptions.IgnoreCase)]
    private static partial Regex TitleVobRegex();

    [GeneratedRegex(@"^VTS_\d\d_0\.IFO$", RegexOptions.IgnoreCase)]
    private static partial Regex TitleIfoRegex();

    [GeneratedRegex(@"^(VIDEO_TS|VTS_\d\d_\d)\.(VOB|IFO|BUP)$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscFileRegex();
}
