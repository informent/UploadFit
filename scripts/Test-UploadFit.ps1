param(
    [string]$Dotnet = 'dotnet',
    [string]$EngineArchive,
    [switch]$DownloadEngine
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($EngineArchive -and $DownloadEngine) { throw 'Choose an engine archive or download it, not both.' }
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$runDirectory = Join-Path $artifacts ('validation-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$log = Join-Path $runDirectory 'validation.log'

# Read the same pin as the application. Fail closed if its declaration changes.
$engineSource = Get-Content -LiteralPath (Join-Path $root 'src/UploadFit.Core/EngineInstaller.cs') -Raw
function Read-EngineConstant([string]$Name) {
    $match = [regex]::Match($engineSource, 'public const string ' + [regex]::Escape($Name) + ' = "([^"]+)";')
    if (-not $match.Success) { throw ('Cannot read engine pin: ' + $Name) }
    return $match.Groups[1].Value
}

function Invoke-Dotnet([string[]]$Arguments) {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $lines = @(& $Dotnet @Arguments 2>&1)
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousPreference }
    $lines | Tee-Object -FilePath $log -Append | Out-Host
    if ($code -ne 0) { throw ('dotnet failed with exit code ' + $code + '. See ' + $log) }
}

$engineVersion = Read-EngineConstant 'Version'
$archiveHash = Read-EngineConstant 'ArchiveHash'
$archiveVerified = $false
$testArguments = @('run', '--project', (Join-Path $root 'tests/UploadFit.Tests'), '-c', 'Release', '-p:UseSharedCompilation=false')

if ($DownloadEngine) {
    $EngineArchive = Join-Path $runDirectory 'engine.zip'
    $uri = Read-EngineConstant 'DownloadUrl'
    Write-Output ('Downloading the pinned engine from ' + $uri)
    Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $EngineArchive
}
if ($EngineArchive) {
    $EngineArchive = (Resolve-Path -LiteralPath $EngineArchive).Path
    if ((Get-FileHash -LiteralPath $EngineArchive -Algorithm SHA256).Hash -ine $archiveHash) {
        throw 'The engine archive does not match the application pin. Nothing was executed.'
    }
    $archiveVerified = $true
    $engineDirectory = Join-Path $runDirectory 'engine'
    Expand-Archive -LiteralPath $EngineArchive -DestinationPath $engineDirectory
    $engineBin = Join-Path $engineDirectory ('ffmpeg-' + $engineVersion + '-essentials_build/bin')
    $testArguments += @('--', $engineBin, $EngineArchive)
}

Invoke-Dotnet @('build', (Join-Path $root 'src/UploadFit.App'), '-c', 'Release', '--disable-build-servers', '-m:1')
Invoke-Dotnet $testArguments
$desktopCount = 0
if ($archiveVerified) {
    Invoke-Dotnet @('build', (Join-Path $root 'tests/UploadFit.DesktopTests'), '-c', 'Release', '--disable-build-servers', '-m:1')
    Invoke-Dotnet @((Join-Path $root 'tests/UploadFit.DesktopTests/bin/Release/net10.0-windows/UploadFit.DesktopTests.dll'), $engineBin)
    $desktopMatch = [regex]::Match((Get-Content -LiteralPath $log -Raw), '(?m)^PASS (\d+) desktop workflow tests')
    if (-not $desktopMatch.Success) { throw 'The desktop test executable did not report a final result.' }
    $desktopCount = [int]$desktopMatch.Groups[1].Value
}
$testText = Get-Content -LiteralPath $log -Raw
$testCount = [regex]::Match($testText, '(?m)^PASS (\d+) tests')
if (-not $testCount.Success) { throw 'The regression executable did not report a final result.' }

$sourceHashes = [ordered]@{}
foreach ($folder in @('src', 'tests', 'scripts')) {
    Get-ChildItem -LiteralPath (Join-Path $root $folder) -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        Sort-Object FullName | ForEach-Object {
            $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
            $sourceHashes[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
}
$sourceHashes['Directory.Build.props'] = (Get-FileHash -LiteralPath (Join-Path $root 'Directory.Build.props') -Algorithm SHA256).Hash.ToLowerInvariant()
$report = [ordered]@{
    product = 'UploadFit'
    checkedAtUtc = [DateTime]::UtcNow.ToString('o')
    operatingSystem = [Environment]::OSVersion.VersionString
    configuration = 'Release'
    testCount = [int]$testCount.Groups[1].Value
    desktopWorkflowTests = $desktopCount
    desktopWorkflowMode = 'in-process, no interactive input or screenshots'
    mediaIntegration = $archiveVerified
    pinnedEngine = $engineVersion
    engineArchiveSha256 = if ($archiveVerified) { $archiveHash } else { $null }
    desktopAcceptance = 'pending'
    selfContainedPackage = 'pending'
    sourceFiles = $sourceHashes
}
$reportPath = Join-Path $runDirectory 'VALIDATION.json'
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 5) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output ('PASS: validation report at ' + $reportPath)
