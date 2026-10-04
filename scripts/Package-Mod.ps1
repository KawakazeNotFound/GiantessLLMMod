param(
    [string]$OutputDirectory = 'bin/ci/dist'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$destination = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $root 'bin')) + [IO.Path]::DirectorySeparatorChar
if (-not $destination.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package output must be a new directory inside this repository bin directory.'
}
if (Test-Path -LiteralPath $destination) {
    throw "Package output already exists. Choose a fresh OutputDirectory: $destination"
}

# Only runtime plugin files are distributed, never the game/Unity/BepInEx reference DLLs.
$pluginPath = 'BepInEx/plugins/GiantessLLMMod'
$files = @(
    @{ Source = 'bin/Release/net472/GiantessLLMMod.dll'; Target = "$pluginPath/GiantessLLMMod.dll" },
    @{ Source = 'bin/Release/net472/Newtonsoft.Json.dll'; Target = "$pluginPath/Newtonsoft.Json.dll" },
    @{ Source = 'llm_system_prompt.conf'; Target = "$pluginPath/llm_system_prompt.conf" },
    @{ Source = 'install-ollama.bat'; Target = 'install-ollama.bat' },
    @{ Source = 'install-ollama.ps1'; Target = 'install-ollama.ps1' },
    @{ Source = 'README.md'; Target = 'README.md' },
    @{ Source = 'THIRD_PARTY_NOTICES.md'; Target = 'THIRD_PARTY_NOTICES.md' }
)

foreach ($file in $files) {
    $source = Join-Path $root $file.Source
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required package input is missing: $source"
    }
}

# Reject invalid/delay-signed reference copies even if the local CLR accepts them.
if (-not ('GiantessLLMMod.Build.SignatureVerifier' -as [type])) {
    Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
namespace GiantessLLMMod.Build
{
    public static class SignatureVerifier
    {
        [DllImport("mscoree.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool StrongNameSignatureVerificationEx(
            string path,
            [MarshalAs(UnmanagedType.Bool)] bool forceVerification,
            [MarshalAs(UnmanagedType.Bool)] out bool wasVerified);
    }
}
'@
}
$verified = $false
$jsonPath = Join-Path $root 'bin/Release/net472/Newtonsoft.Json.dll'
$signatureValid = [GiantessLLMMod.Build.SignatureVerifier]::StrongNameSignatureVerificationEx($jsonPath, $true, [ref]$verified)
if (-not $signatureValid -or -not $verified) {
    throw 'Newtonsoft.Json strong-name signature is invalid. Restore the official NuGet package before packaging.'
}

$commit = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40,64}$') {
    throw 'Could not identify the package source commit.'
}

foreach ($file in $files) {
    $target = Join-Path $destination $file.Target
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $file.Source) -Destination $target
}

$metadata = [ordered]@{
    Commit = $commit
    BuiltAtUtc = [DateTime]::UtcNow.ToString('o')
    PluginTarget = 'net472'
    PluginVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $destination "$pluginPath/GiantessLLMMod.dll")).Version.ToString()
    JsonVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $destination "$pluginPath/Newtonsoft.Json.dll")).Version.ToString()
    GitHubRunId = $env:GITHUB_RUN_ID
    GitHubRunAttempt = $env:GITHUB_RUN_ATTEMPT
}
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'BUILD_INFO.json') -Encoding UTF8

$relativeFiles = @($files | ForEach-Object { $_.Target }) + @('BUILD_INFO.json')
$hashes = foreach ($relativePath in ($relativeFiles | Sort-Object)) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $destination $relativePath) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $relativePath"
}
$hashes | Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') -Encoding UTF8

$expectedFiles = @($relativeFiles) + @('SHA256SUMS.txt')
$actualFiles = @(Get-ChildItem -LiteralPath $destination -Recurse -File)
if ($actualFiles.Count -ne $expectedFiles.Count) {
    throw 'Installation package file count does not match the explicit manifest.'
}
Write-Host "Validated $($actualFiles.Count) package files at $destination (commit $commit)."
