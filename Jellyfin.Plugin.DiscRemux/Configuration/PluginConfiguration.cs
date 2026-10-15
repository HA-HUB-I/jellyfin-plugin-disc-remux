using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DiscRemux.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether every library folder is scanned for disc rips.</summary>
    public bool ScanLibraries { get; set; } = true;

    /// <summary>
    /// Gets or sets extra folders (one per line) that hold complete DVD releases, typically the download client folder.
    /// They are used to find the full disc behind a single VOB that Radarr/Sonarr imported. Files there are never modified.
    /// </summary>
    public string SourcePaths { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a newly added VOB triggers processing right away.</summary>
    public bool ProcessOnItemAdded { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether only a report is logged and nothing is written.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets a value indicating whether a stereo AAC track (made from the first audio track) is added as the default track.</summary>
    public bool AddStereoAac { get; set; } = true;

    /// <summary>Gets or sets the AAC bitrate in kbit/s.</summary>
    public int AacBitrate { get; set; } = 192;

    /// <summary>Gets or sets a value indicating whether bitmap DVD subtitles are OCR'd to SRT with tesseract.</summary>
    public bool OcrSubtitles { get; set; } = true;

    /// <summary>Gets or sets the subtitle languages (ISO 639-1, comma separated) to OCR. Empty means every language with an installed tesseract model.</summary>
    public string OcrLanguages { get; set; } = string.Empty;

    /// <summary>Gets or sets the tesseract executable.</summary>
    public string TesseractPath { get; set; } = "tesseract";

    /// <summary>Gets or sets a value indicating whether bitmap subtitle tracks that were converted to SRT are dropped from the MKV.</summary>
    public bool DropConvertedBitmapSubtitles { get; set; } = true;

    /// <summary>Gets or sets the preferred subtitle language (ISO 639-1); its SRT is marked default. Empty disables.</summary>
    public string DefaultSubtitleLanguage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where replaced library files are moved. Empty means a hidden ".disc-remux-original" folder next to the movie.
    /// </summary>
    public string TrashPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the allowed ratio between the joined VOB duration and the DVD title duration before a disc is skipped (seamless branching guard).</summary>
    public double MaxDurationMismatch { get; set; } = 0.05;
}
