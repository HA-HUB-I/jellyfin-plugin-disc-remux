using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DiscRemux.Dvd;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscRemux;

/// <summary>Processes a folder shortly after Jellyfin adds a VTS_xx_x.VOB from it, before the download client can clean up the full disc.</summary>
public sealed class ItemAddedListener : IHostedService, IDisposable
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(1);

    private readonly ILibraryManager _libraryManager;
    private readonly DiscRemuxService _service;
    private readonly ILogger<ItemAddedListener> _logger;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();

    public ItemAddedListener(ILibraryManager libraryManager, DiscRemuxService service, ILogger<ItemAddedListener> logger)
    {
        _libraryManager = libraryManager;
        _service = service;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        await _stopping.CancelAsync().ConfigureAwait(false);
    }

    public void Dispose() => _stopping.Dispose();

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var path = e.Item?.Path;
        if (Plugin.Instance?.Configuration.ProcessOnItemAdded != true || string.IsNullOrEmpty(path) || !DiscLocator.IsTitleVob(Path.GetFileName(path)))
        {
            return;
        }

        var folder = Path.GetDirectoryName(path)!;
        if (!_pending.TryAdd(folder, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SettleDelay, _stopping.Token).ConfigureAwait(false);
                _logger.LogInformation("Disc Remux: new DVD file in {Folder}, processing", folder);
                await _service.ProcessFolderAsync(folder, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Disc Remux: processing {Folder} failed", folder);
            }
            finally
            {
                _pending.TryRemove(folder, out _);
            }
        });
    }
}
