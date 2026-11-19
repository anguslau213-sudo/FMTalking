param(
    [string]$GameDir = $env:FM27_GAME_DIR,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'compiled/releases')
)

$ErrorActionPreference = 'Stop'
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('FMPlayerTalk-release-' + [Guid]::NewGuid().ToString('N'))
$build = Join-Path $scratch 'build'
& (Join-Path $PSScriptRoot 'build.ps1') -GameDir $GameDir -OutputDirectory $build | Out-Host

$plugin = Join-Path $build 'FmPlayerTalk.dll'
$metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($plugin)
$buildVersion = [Reflection.AssemblyName]::GetAssemblyName($plugin).Version.ToString(3)
$version = $metadata.ProductVersion
$zipPath = Join-Path $destination "$($metadata.ProductName)-$version.zip"
if ([IO.File]::Exists($zipPath)) {
    throw "Release package already exists: $zipPath"
}

$licenses = Join-Path $PSScriptRoot 'third-party/zstdsharp-0.8.8'
$readme = Join-Path $PSScriptRoot 'docs/INSTALL.md'
$agentGuide = Join-Path $PSScriptRoot 'AGENTS.md'
$entries = [ordered]@{
    'AGENTS.md' = $agentGuide
    'README.md' = $readme
    'BepInEx/plugins/FmPlayerTalk/AGENTS.md' = $agentGuide
    'BepInEx/plugins/FmPlayerTalk/README.md' = $readme
    'BepInEx/plugins/FmPlayerTalk/FmPlayerTalk.dll' = $plugin
    'BepInEx/plugins/FmPlayerTalk/ZstdSharp.dll' = Join-Path $build 'ZstdSharp.dll'
    'BepInEx/plugins/FmPlayerTalk/ZstdSharp-LICENSE.txt' = Join-Path $licenses 'LICENSE.txt'
    'BepInEx/plugins/FmPlayerTalk/Zstandard-LICENSE.txt' = Join-Path $licenses 'Zstandard-LICENSE.txt'
    'BepInEx/plugins/FmPlayerTalk/ZstdSharp-SOURCE.txt' = Join-Path $licenses 'SOURCE.txt'
}
$projectLicense = Join-Path $PSScriptRoot 'LICENSE'
if (Test-Path -LiteralPath $projectLicense -PathType Leaf) {
    $entries['BepInEx/plugins/FmPlayerTalk/FMTalking-LICENSE.txt'] = $projectLicense
}
$zipTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
$stagedZip = Join-Path $scratch 'release.zip'
$archive = [IO.Compression.ZipFile]::Open($stagedZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $entries.GetEnumerator()) {
        $zipEntry = $archive.CreateEntry($entry.Key, [IO.Compression.CompressionLevel]::Optimal)
        $zipEntry.LastWriteTime = $zipTime
        $bytes = [IO.File]::ReadAllBytes($entry.Value)
        $packed = $zipEntry.Open()
        try {
            $packed.Write($bytes, 0, $bytes.Length)
        } finally {
            $packed.Dispose()
        }
    }
} finally {
    $archive.Dispose()
}

# Extract into an empty folder without touching any installed copy or player state.
$clean = Join-Path $scratch 'clean'
[IO.Compression.ZipFile]::ExtractToDirectory($stagedZip, $clean)
$files = @(Get-ChildItem -LiteralPath $clean -Recurse -Force -File)
if ($files.Count -ne $entries.Count) {
    throw 'The extracted package contains an unexpected number of files.'
}
foreach ($entry in $entries.GetEnumerator()) {
    $expected = (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash
    $installedFile = Join-Path $clean $entry.Key
    if ((Get-FileHash -LiteralPath $installedFile -Algorithm SHA256).Hash -ne $expected) {
        throw "The extracted package did not preserve $($entry.Key)."
    }
}

$null = [IO.Directory]::CreateDirectory($destination)
[IO.File]::Copy($stagedZip, $zipPath)
[PSCustomObject]@{
    Product = $metadata.ProductName
    Version = $version
    Build = $buildVersion
    Package = $zipPath
    SHA256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    ExtractedFilesVerified = $files.Count
    CleanExtraction = $clean
}
