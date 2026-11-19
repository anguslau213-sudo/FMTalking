using System.Reflection;

[assembly: AssemblyVersion(FmPlayerTalk.PluginVersion.Build + ".0")]
[assembly: AssemblyFileVersion(FmPlayerTalk.PluginVersion.Build + ".0")]
[assembly: AssemblyInformationalVersion(FmPlayerTalk.PluginVersion.Release)]
[assembly: AssemblyProduct(FmPlayerTalk.PluginVersion.Name)]
[assembly: AssemblyCopyright(FmPlayerTalk.PluginVersion.Credit)]

namespace FmPlayerTalk;

internal static class PluginVersion
{
    internal const string Name = "FMTalking";
    internal const string Release = "1.0";
    internal const string Build = "1.0.29";
    internal const string Credit = "蒸汽波函数（Angus·Liu）& Codex 2026";
}
