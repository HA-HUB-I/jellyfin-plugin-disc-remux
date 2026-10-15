using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.DiscRemux.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DiscRemux;

/// <summary>
/// Disc Remux: turns DVD folder rips (VIDEO_TS) and split VOB imports into a single, client friendly MKV.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginId = Guid.Parse("1bad9bcc-8666-46aa-a223-3d700d7b1fa2");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Disc Remux";

    public override Guid Id => PluginId;

    public override string Description =>
        "Converts DVD rips (VIDEO_TS folders and split VTS_xx_x.VOB imports) into a single MKV with a stereo AAC track and OCR'd SRT subtitles.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
