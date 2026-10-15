using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DiscRemux.Configuration;
using Jellyfin.Plugin.DiscRemux.Dvd;
using Jellyfin.Plugin.DiscRemux.Processing;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscRemux;

/// <summary>Finds and converts disc rips; serialises all work so only one ffmpeg job runs at a time.</summary>
public sealed class DiscRemuxService : IDisposable
{
    private static readonly string[] SkippedCollectionTypes = ["music", "books", "photos", "playlists"];

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<DiscRemuxService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public DiscRemuxService(ILibraryManager libraryManager, IMediaEncoder mediaEncoder, IApplicationPaths appPaths, ILogger<DiscRemuxService> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _appPaths = appPaths;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public async Task RunAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = Config;
            var locator = new DiscLocator(_logger);
            var roots = config.ScanLibraries ? GetLibraryRoots() : [];
            var candidates = locator.Find(roots, GetSourceRoots(config));
            _logger.LogInformation("Disc Remux: {Count} disc rip(s) found", candidates.Count);
            bool changed = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                changed |= await ProcessOneAsync(candidates[i], config, cancellationToken).ConfigureAwait(false);
                progress.Report(100.0 * (i + 1) / candidates.Count);
            }

            if (changed)
            {
                _libraryManager.QueueLibraryScan();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ProcessFolderAsync(string folder, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = Config;
            var candidate = new DiscLocator(_logger).InspectFolder(folder, GetLibraryRoots(), GetSourceRoots(config));
            if (candidate is not null && await ProcessOneAsync(candidate, config, cancellationToken).ConfigureAwait(false))
            {
                _libraryManager.QueueLibraryScan();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    private async Task<bool> ProcessOneAsync(DiscCandidate candidate, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var remuxer = new DiscRemuxer(_logger, _mediaEncoder.EncoderPath, _mediaEncoder.ProbePath, _appPaths.TempDirectory);
        try
        {
            var result = await remuxer.ProcessAsync(candidate, config, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Disc Remux: {Message}", result.Message);
            return result.Changed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Disc Remux: failed on {Folder}", candidate.DiscFolder);
            return false;
        }
    }

    private List<string> GetLibraryRoots()
    {
        return _libraryManager.GetVirtualFolders()
            .Where(f => f.CollectionType is null || !SkippedCollectionTypes.Contains(f.CollectionType.ToString()!.ToLowerInvariant()))
            .SelectMany(f => f.Locations)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> GetSourceRoots(PluginConfiguration config)
    {
        return config.SourcePaths
            .Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
