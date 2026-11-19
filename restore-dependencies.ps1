$ErrorActionPreference = 'Stop'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('FMTalking-dependencies-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($scratch)

function Expand-VerifiedPackage ($Id, $Version, $Hash) {
    $filename = "$Id.$Version.nupkg"
    $package = Join-Path $scratch $filename
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$Id/$Version/$filename" -OutFile $package
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $Hash) {
        throw "SHA-256 mismatch for $filename."
    }
    $expanded = Join-Path $scratch $Id
    [IO.Compression.ZipFile]::ExtractToDirectory($package, $expanded)
    return $expanded
}

$net = Expand-VerifiedPackage 'microsoft.netcore.app.ref' '6.0.36' 'F4B66056820517CA8DC94BBC91D26B6062EEB4C17F70BD8AF361CDDB2C30971F'
$zstd = Expand-VerifiedPackage 'zstdsharp.port' '0.8.8' 'F6797192DEEBF4535F0601C2273E750DA0C02062E30A3E4A3C4011B9AC22403A'
$referenceDir = Join-Path $PSScriptRoot 'net6-reference/ref/net6.0'
$null = [IO.Directory]::CreateDirectory($referenceDir)
Get-ChildItem -LiteralPath (Join-Path $net 'ref/net6.0') -File -Filter '*.dll' | Copy-Item -Destination $referenceDir -Force
Copy-Item -LiteralPath (Join-Path $zstd 'lib/net6.0/ZstdSharp.dll') -Destination (Join-Path $PSScriptRoot 'third-party/zstdsharp-0.8.8') -Force
Write-Output 'Restored pinned .NET and ZstdSharp build dependencies.'
