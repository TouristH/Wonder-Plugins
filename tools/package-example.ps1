#requires -Version 7.4
# SPDX-License-Identifier: MIT
param([Parameter(Mandatory)][string]$WonderExtensibilityProject)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sdk = [IO.Path]::GetFullPath($WonderExtensibilityProject)
$contracts = Join-Path (Split-Path (Split-Path $sdk -Parent) -Parent) 'Wonder.Extensibility.Contracts/bin/Release/net10.0/Wonder.Extensibility.Contracts.dll'
$registry = Join-Path $root 'src/Wonder.PluginRegistry/Wonder.PluginRegistry.csproj'
dotnet build $registry -c Release "-p:WonderExtensibilityProject=$sdk" --nologo
if ($LASTEXITCODE) { throw 'Registry build failed.' }
$sample = Join-Path $root 'examples/Wonder.Example.Hello'
dotnet build (Join-Path $sample 'Wonder.Example.Hello.csproj') -c Release "-p:WonderContractsPath=$contracts" -p:DebugType=None -p:DebugSymbols=false --nologo
if ($LASTEXITCODE) { throw 'Example build failed.' }
$manifest = Get-Content -LiteralPath (Join-Path $sample 'plugin.json') -Raw | ConvertFrom-Json
$stage = Join-Path $root ('artifacts/example-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $stage 'source') -Force | Out-Null
foreach ($name in @('plugin.json','LICENSE','ADDITIONAL_TERMS.md','BRANDING.md','NOTICE','RISK.md')) {
    Copy-Item -LiteralPath (Join-Path $sample $name) -Destination $stage
}
foreach ($name in @('plugin.json','HelloPlugin.cs','Wonder.Example.Hello.csproj')) {
    Copy-Item -LiteralPath (Join-Path $sample $name) -Destination (Join-Path $stage 'source')
}
Copy-Item -LiteralPath (Join-Path $sample ('bin/Release/net10.0/' + $manifest.entryAssembly)) -Destination $stage
$cli = Join-Path $root 'src/Wonder.PluginRegistry/bin/Release/net10.0/Wonder.PluginRegistry.dll'
dotnet $cli report --generate $stage
if ($LASTEXITCODE) { throw 'Report generation failed.' }
dotnet $cli report --verify $stage
if ($LASTEXITCODE) { throw 'Report verification failed.' }
$destination = Join-Path $root ('packages/' + $manifest.id + '-' + $manifest.version + '.zip')
# Never overwrite a published version with different bytes.
$temporary = Join-Path $stage 'package.zip.tmp'
$archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object FullName -ne $temporary | Sort-Object FullName)) {
        $name = [IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\','/')
        $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
        $inputFile = $file.OpenRead()
        $output = $entry.Open()
        try { $inputFile.CopyTo($output) } finally { $output.Dispose(); $inputFile.Dispose() }
    }
} finally { $archive.Dispose() }
if (Test-Path -LiteralPath $destination) {
    if ((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $temporary).Hash) { throw 'Version exists with different bytes; bump the example version.' }
} else { Copy-Item -LiteralPath $temporary -Destination $destination }
dotnet $cli check-package $destination
if ($LASTEXITCODE) { throw 'Package verification failed.' }
Write-Output $destination
