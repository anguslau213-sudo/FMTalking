param(
    [switch]$VerifyConversation,
    [string]$GameDir = $env:FM27_GAME_DIR,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'compiled')
)

$ErrorActionPreference = 'Stop'
$build = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$referencePack = Join-Path $PSScriptRoot 'net6-reference\ref\net6.0'
if (!(Test-Path -LiteralPath (Join-Path $referencePack 'System.Runtime.dll') -PathType Leaf)) {
    throw 'Missing .NET reference pack. Run restore-dependencies.ps1 first.'
}
$null = [IO.Directory]::CreateDirectory($build)

[Reflection.Assembly]::LoadFrom((Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')) | Out-Null

$references = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($file in (Get-ChildItem -LiteralPath $referencePack -Filter '*.dll')) {
    $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$sources = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$parser = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp10)
if ($VerifyConversation) {
    $files = @(
        Get-Item (Join-Path $PSScriptRoot 'src\PluginVersion.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\ModDataPath.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\UiLanguage.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\UiLanguage.Text.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\UiLanguage.SettingsText.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\Conversation.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\Conversation.GameActions.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\Conversation.HistoryArchive.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\Conversation.PromiseFollowUps.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\Conversation.PlayerIntroductions.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\PlayerIntroductionRules.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\PlayerIntroductionNameResolver.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\FmNativeLayout.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\FmRelationBufferAllocation.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\Conversation.RelationshipMessages.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\RelationshipEventRules.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\ModelConnection.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\ConversationRules.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\BoardFundingRules.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\ShadowBoardRules.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\PromiseWaiverLanguage.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\RoleBoundary.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\SocialBoundary.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\SaveCheckpoints.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\SaveCheckpoints.Migration.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\BoardFundingLedger.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\DraftBuffer.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\ClubDirectory.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\NativeNameTable.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\FmDisplayText.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\DepartedContacts.cs')
        Get-Item (Join-Path $PSScriptRoot 'src\OtherIncomeThreshold.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\ConversationSmoke.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\ConversationFixtures.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\RelationshipMessageSmoke.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\BoardFundingSmoke.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\ShadowBoardSmoke.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\RelationBufferAllocationSmoke.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\PromiseFollowUpSmoke.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\PromiseProtocolSmoke.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\GameActionContextSmoke.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\HistoryArchiveSmoke.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\NativeNameTableSmoke.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\NativeAffinityConversationSmoke.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\DraftBufferSmoke.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\DirectorySmoke.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\UiLanguageSmoke.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\SaveCheckpointSmoke.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\SaveHistoryMigrationSmoke.cs')
        Get-Item (Join-Path $PSScriptRoot 'verify\OtherIncomeThresholdSmoke.cs')
    )
} else {
    if ([string]::IsNullOrWhiteSpace($GameDir) -and ${env:ProgramFiles(x86)}) {
        $GameDir = Join-Path ${env:ProgramFiles(x86)} 'Steam/steamapps/common/Football Manager 27'
    }
    if ([string]::IsNullOrWhiteSpace($GameDir) -or !(Test-Path -LiteralPath $GameDir -PathType Container)) {
        throw 'FM 27 game directory not found. Use -GameDir or set FM27_GAME_DIR to the game directory.'
    }
    $game = Join-Path (Resolve-Path -LiteralPath $GameDir).Path 'BepInEx'
    $files = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs'
    $compression = Join-Path $PSScriptRoot 'third-party\zstdsharp-0.8.8\ZstdSharp.dll'
    if (!(Test-Path -LiteralPath $compression -PathType Leaf)) {
        throw 'Missing ZstdSharp. Run restore-dependencies.ps1 first.'
    }
    $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($compression))
    foreach ($relative in @(
        'core\BepInEx.Core.dll',
        'core\BepInEx.Unity.IL2CPP.dll',
        'core\Il2CppInterop.Runtime.dll',
        'core\0Harmony.dll',
        'interop\Il2Cppmscorlib.dll',
        'interop\UnityEngine.CoreModule.dll',
        'interop\UnityEngine.ImageConversionModule.dll',
        'interop\UnityEngine.UIElementsModule.dll',
        'interop\UnityEngine.TextCoreTextEngineModule.dll',
        'interop\UnityEngine.TextRenderingModule.dll',
        'interop\UnityEngine.IMGUIModule.dll',
        'interop\UnityEngine.InputLegacyModule.dll',
        'interop\Unity.InputSystem.dll',
        'interop\SI.Core.dll',
        'interop\SI.Input.dll',
        'interop\SI.UI.dll',
        'interop\SI.Bindable.dll',
        'interop\SI.Interop.dll',
        'interop\FM.GamePlugin.dll',
        'interop\FM.UI.dll'
    )) {
        $assemblyPath = Join-Path $game $relative
        if (!(Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
            throw "Missing build reference: $assemblyPath. Use a BepInEx IL2CPP installation with generated FM 27 interop assemblies."
        }
        $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($assemblyPath))
    }
}
foreach ($file in $files) {
    $sources.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file.FullName), $parser, $file.FullName))
}
$metadata = @'
[assembly: System.Runtime.Versioning.TargetFramework(".NETCoreApp,Version=v6.0", FrameworkDisplayName = ".NET 6.0")]
'@
$sources.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($metadata, $parser))
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$options = $options.WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithDeterministic($true)
$name = if ($VerifyConversation) { 'FmPlayerTalk.Validation' } else { 'FmPlayerTalk' }
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($name, $sources, $references, $options)
$memory = [IO.MemoryStream]::new()
$versionResource = $compilation.CreateDefaultWin32Resources($true, $true, $null, $null)
try {
    $result = $compilation.Emit($memory, $null, $null, $versionResource, $null, $null, [Threading.CancellationToken]::None)
    $result.Diagnostics | Where-Object Severity -ge Warning | ForEach-Object { $_.ToString() }
    if (!$result.Success) { throw 'Compilation failed' }

    $path = Join-Path $build ($name + '.dll')
    [IO.File]::WriteAllBytes($path, $memory.ToArray())
    if (!$VerifyConversation) {
        [IO.File]::Copy($compression, (Join-Path $build 'ZstdSharp.dll'), $true)
    }
    Get-Item -LiteralPath $path | Select-Object Name, Length
    if ($VerifyConversation) {
        $checks = [Reflection.Assembly]::LoadFrom($path)
        $checks.GetType('RelationshipMessageSmoke').GetMethod('Run').Invoke($null, $null)
        foreach ($test in @('ConversationSmoke', 'PromiseFollowUpSmoke', 'PromiseProtocolSmoke', 'GameActionContextSmoke', 'HistoryArchiveSmoke', 'NativeNameTableSmoke', 'NativeAffinityConversationSmoke', 'DraftBufferSmoke', 'DirectorySmoke', 'UiLanguageSmoke', 'SaveCheckpointSmoke', 'SaveHistoryMigrationSmoke', 'OtherIncomeThresholdSmoke', 'BoardFundingSmoke', 'RelationBufferAllocationSmoke')) {
            $checks.GetType($test).GetMethod('Run').Invoke($null, $null)
        }
    }
} finally {
    $versionResource.Dispose()
    $memory.Dispose()
}
