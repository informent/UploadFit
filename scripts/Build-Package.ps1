param(
    [Parameter(Mandatory=$true)][string]$ValidationReport,
    [string]$Dotnet = 'dotnet',
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot -Parent
$report = Get-Content -LiteralPath $ValidationReport -Raw | ConvertFrom-Json
if ($report.product -cne 'UploadFit' -or -not $report.mediaIntegration -or $report.desktopWorkflowTests -lt 1) {
    throw 'Run the full media and desktop workflow checks before packaging.'
}

# Reject a stale or incomplete report before compiling a distribution.
$currentFiles = @(
    foreach ($folder in @('src', 'tests', 'scripts')) {
        Get-ChildItem -LiteralPath (Join-Path $root $folder) -File -Recurse |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    }
    Get-Item -LiteralPath (Join-Path $root 'Directory.Build.props')
)
if (@($report.sourceFiles.PSObject.Properties).Count -ne $currentFiles.Count) { throw 'The validated source file set changed. Run validation again.' }
foreach ($file in $currentFiles) {
    $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
    $recorded = $report.sourceFiles.PSObject.Properties[$relative]
    if ($null -eq $recorded -or $recorded.Value -ine (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
        throw ('Source changed after validation: ' + $relative)
    }
}

[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Unexpected application version.' }
$output = Join-Path $root ('artifacts/package-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $output 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
& $Dotnet publish (Join-Path $root 'src/UploadFit.App') -c Release -r win-x64 --self-contained $selfContained --disable-build-servers -m:1 -o $payload
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed. No package was created.' }
foreach ($name in @('UploadFit.exe', 'UploadFit.dll', 'UploadFit.Core.dll', 'UploadFit.deps.json', 'UploadFit.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) { throw ('Missing package file: ' + $name) }
}
if (-not $FrameworkDependent) {
    foreach ($name in @('coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) { throw ('Missing bundled runtime file: ' + $name) }
    }
}
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'UploadFit.exe'))
if ($info.ProductName -cne 'UploadFit' -or ($info.ProductVersion -split '\+')[0] -cne $version) { throw 'Executable branding or version mismatch.' }
foreach ($name in @('LICENSE', 'THIRD-PARTY.md', 'README.md', 'VALIDATION.md')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $payload
}
$requirements = if ($FrameworkDependent) {
    'This development package requires the Microsoft .NET 10 Desktop Runtime (Windows x64). It does not bundle the runtime.'
} else { 'This preview bundles the .NET runtime. No separate .NET installation is required.' }
$startHere = @"
UploadFit $version preview

Extract the entire ZIP into a folder, then open UploadFit.exe.
$requirements

The first run offers a download of the pinned FFmpeg video engine. You can also import its exact ZIP. Videos are processed on your computer; originals are kept intact.

Choose an output size, add local clips, review the trim, and export. Check the finished video before sharing it.

This unsigned preview has automated media and in-process app checks. Interactive desktop and visual acceptance remain pending. See VALIDATION.md for the exact limits.
"@
[IO.File]::WriteAllText((Join-Path $payload 'START-HERE.txt'), $startHere + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

$entries = [ordered]@{}
Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($payload.Length + 1).Replace('\', '/')
    $entries[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifest = [ordered]@{ product='UploadFit'; version=$version; runtimeBundled=(-not [bool]$FrameworkDependent); files=$entries }
[IO.File]::WriteAllText((Join-Path $payload 'PACKAGE-MANIFEST.json'), ($manifest | ConvertTo-Json -Depth 5) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$suffix = if ($FrameworkDependent) { '-framework-dependent' } else { '' }
$zipName = 'UploadFit-v' + $version + '-win-x64' + $suffix + '-preview.zip'
$zip = Join-Path $output $zipName
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$extracted = Join-Path $output 'extracted-check'
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $extracted)
foreach ($entry in $entries.GetEnumerator()) {
    if ((Get-FileHash -LiteralPath (Join-Path $extracted $entry.Key) -Algorithm SHA256).Hash -ine $entry.Value) { throw ('Extracted file checksum failed: ' + $entry.Key) }
}
if (@(Get-ChildItem -LiteralPath $extracted -File -Recurse).Count -ne $entries.Count + 1) { throw 'Unexpected extracted package contents.' }

# Start the extracted application's diagnostic mode, without opening a window.
$diagnosticPath = Join-Path $output 'startup-diagnostic.json'
$startFile = if ($FrameworkDependent) { $Dotnet } else { Join-Path $extracted 'UploadFit.exe' }
$arguments = @()
if ($FrameworkDependent) { $arguments += ('"' + (Join-Path $extracted 'UploadFit.dll') + '"') }
$arguments += @('--check-package', ('"' + $diagnosticPath + '"'))
$process = Start-Process -FilePath $startFile -ArgumentList $arguments -PassThru -WindowStyle Hidden
try {
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Package startup check timed out.' }
    if ($process.ExitCode -ne 0) { throw ('Package startup check failed: ' + $process.ExitCode) }
} finally { $process.Dispose() }
$diagnostic = Get-Content -LiteralPath $diagnosticPath -Raw | ConvertFrom-Json
if ($diagnostic.product -cne 'UploadFit' -or $diagnostic.version -cne $version -or $diagnostic.architecture -cne 'X64' -or
    -not $diagnostic.resourcesLoaded -or -not $diagnostic.coreLoaded) { throw 'Extracted application diagnostic did not match the package.' }
$packageReport = [ordered]@{
    product = 'UploadFit'
    version = $version
    runtimeBundled = (-not [bool]$FrameworkDependent)
    signed = $false
    mediaTests = $report.testCount
    desktopWorkflowTests = $report.desktopWorkflowTests
    extractedFilesVerified = $entries.Count
    startupDiagnostic = $diagnostic
    sourceValidationSha256 = (Get-FileHash -LiteralPath $ValidationReport -Algorithm SHA256).Hash.ToLowerInvariant()
    desktopAcceptance = 'pending'
    publicRelease = 'not published'
}
$packageReportPath = Join-Path $output 'PACKAGE-VALIDATION.json'
[IO.File]::WriteAllText($packageReportPath, ($packageReport | ConvertTo-Json -Depth 5) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$checksums = foreach ($name in @($zipName, 'PACKAGE-VALIDATION.json')) {
    (Get-FileHash -LiteralPath (Join-Path $output $name) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $name
}
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS'), ($checksums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Output ('PASS: verified preview package at ' + $zip)
