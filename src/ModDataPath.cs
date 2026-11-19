using System;
using System.IO;

namespace FmPlayerTalk;

internal static class ModDataPath
{
    internal static string Root { get; } = Resolve();

    private static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("FM_PLAYER_TALK_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured))
        {
            try { return Path.GetFullPath(configured); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sports Interactive", "Football Manager 27", "FmPlayerTalk");
    }
}
