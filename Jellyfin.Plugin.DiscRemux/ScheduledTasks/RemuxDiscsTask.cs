using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.DiscRemux.ScheduledTasks;

/// <summary>Dashboard → Scheduled Tasks → "Convert DVD rips to MKV".</summary>
public sealed class RemuxDiscsTask : IScheduledTask
{
    private readonly DiscRemuxService _service;

    public RemuxDiscsTask(DiscRemuxService service)
    {
        _service = service;
    }

    public string Name => "Convert DVD rips to MKV";

    public string Key => "DiscRemuxConvert";

    public string Description => "Finds VIDEO_TS folders and split VOB imports and turns them into one MKV with a stereo AAC track and SRT subtitles.";

    public string Category => "Disc Remux";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => _service.RunAllAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks };
    }
}
