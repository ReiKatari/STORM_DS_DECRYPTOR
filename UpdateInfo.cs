using System;

namespace StormDsDecryptor;

public class UpdateInfo
{
    public Version Version { get; set; } = new(1, 5, 0);
    public string VersionString { get; set; } = "";
    public string Title { get; set; } = "";
    public string Changelog { get; set; } = "";
    public string SetupDownloadUrl { get; set; } = "";
    public string MirrorSetupDownloadUrl { get; set; } = "";
    public string SetupFileName { get; set; } = "";
    public long SetupFileSize { get; set; }
    public string ReleaseUrl { get; set; } = "";
    public string MirrorWebUrl { get; set; } = UpdateManager.MirrorWebUrl;
    public string SourceName { get; set; } = "GitHub";
}
