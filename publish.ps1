# Publish script for MinioSync (replaces the legacy build.ps1 from the
# .NET Framework 4.6.1 era).
#
# Produces a fully self-contained deploy/ directory suitable for distribution:
# - Each output exe (MinioSync.exe / SyncWorker.exe / FullSync.exe) is a single
#   file that already bundles the .NET 8 runtime, the Minio SDK, System.Text.Json,
#   and the project's own dependencies.
# - Target machines do **not** need any pre-installed .NET / runtime.
# - Costs ~65 MB per exe (compressed) on disk; start-up speed trade-off is
#   negligible because all three binaries are long-lived (daemon) or one-shot.
#
# Usage (PowerShell, Windows):
#   .\publish.ps1                       # default: Configuration=Release, Runtime=win-x64
#   .\publish.ps1 -Configuration Debug  # for diagnostics
#
# Equivalent manual command-line equivalent:
#   dotnet restore MinioSync.sln
#   dotnet build   MinioSync.sln -c Release
#   dotnet publish MinioSync\MinioSync.csproj -c Release -r win-x64 --no-build -o deploy\
#   dotnet publish SyncWorker\SyncWorker.csproj -c Release -r win-x64 --no-build -o deploy\
#   dotnet publish FullSync\FullSync.csproj     -c Release -r win-x64 --no-build -o deploy\

[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [string]$Solution = "MinioSync.sln"
)

$ErrorActionPreference = "Stop"

$root        = $PSScriptRoot
$solutionFull = Join-Path $root $Solution
$deployDir   = Join-Path $root "deploy"

if (-not (Test-Path -LiteralPath $solutionFull)) {
    Write-Error "Solution not found: $solutionFull"
    exit 1
}

# Fail fast if dotnet CLI is not available.
try {
    $dotnetVersion = & dotnet --version
}
catch {
    Write-Error "dotnet CLI not found. Please install .NET 8 SDK first.`nDownload: https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
}
Write-Host "dotnet CLI version: $dotnetVersion" -ForegroundColor DarkGray

# Sanity check the SDK major version matches our target framework.
$dotnetMajor = (& dotnet --version).Split('.')[0]
if ($dotnetMajor -ne "8") {
    Write-Warning "Project targets net8.0 but installed dotnet CLI is major version $dotnetMajor. Build may fail."
}

Write-Host "============================================================"
Write-Host "MinioSync Publish (Configuration=$Configuration, Runtime=$Runtime)" -ForegroundColor Cyan
Write-Host "============================================================"

# -------- Step 1: restore --------
# IMPORTANT: pass `-r $Runtime` so that NuGet restore produces win-x64-specific
# asset files (obj\project.assets.json with net8.0/win-x64 target). Without this,
# the subsequent `dotnet publish -r win-x64` fails with NETSDK1047 because
# project.assets.json only contains the RID-neutral "net8.0" entry.
Write-Host ""
Write-Host "[1/4] dotnet restore (-r $Runtime)" -ForegroundColor Yellow
& dotnet restore $solutionFull -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed (exit $LASTEXITCODE)" }

# -------- Step 2: build --------
# NOTE: do NOT pass -r to solution-level build. NETSDK1134 forbids solution-level
# RID. The previous restore (with -r) has already prepared the win-x64 assets, so
# each per-project publish() call below will pick them up.
Write-Host ""
Write-Host "[2/4] dotnet build ($Configuration)" -ForegroundColor Yellow
& dotnet build $solutionFull -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }

# -------- Step 3: wipe deploy/ --------
Write-Host ""
Write-Host "[3/4] Cleaning $deployDir" -ForegroundColor Yellow
if (Test-Path -LiteralPath $deployDir) {
    Remove-Item -LiteralPath $deployDir -Recurse -Force
}
New-Item -ItemType Directory -Path $deployDir | Out-Null

# -------- Step 4: publish each exe project --------
Write-Host ""
Write-Host "[4/4] Publishing self-contained executables" -ForegroundColor Yellow

# Publish-time properties. Passed on the command line (not via Directory.Build.props)
# so that library projects (MinioCommon) are never forced to a specific RID.
$publishProps = @(
    '-p:SelfContained=true'
    '-p:PublishSingleFile=true'
    '-p:EnableCompressionInSingleFile=true'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:PublishReadyToRun=false'      # daemon 进程启动频率低, 关闭省 ~30MB
    '-p:PublishTrimmed=false'          # Newtonsoft.Json/Minio 未标 IsTrimmable, trim 会崩
)

# Flatten: every exe + its assets go into the same deploy/ directory.
# IMPORTANT: do NOT pass --no-build here. Single-file publish (PublishSingleFile=true)
# requires a RID-specific intermediate build because the SDK needs to materialize a
# `singlefilehost.exe` host stub in obj\Release\net8.0\win-x64\ before bundling.
# Without that intermediate file, GenerateBundle fails with FileNotFoundException.
$exes = @("MinioSync", "SyncWorker", "FullSync")
foreach ($name in $exes) {
    $csproj = Join-Path $root "$name\$name.csproj"
    if (-not (Test-Path -LiteralPath $csproj)) { throw "Missing csproj: $csproj" }
    Write-Host "  -> $name (-r $Runtime, --self-contained, --single-file)" -ForegroundColor DarkCyan
    & dotnet publish $csproj `
        --configuration $Configuration `
        --runtime $Runtime `
        --output $deployDir `
        @publishProps
    if ($LASTEXITCODE -ne 0) { throw "Publish $name failed (exit $LASTEXITCODE)" }
}

# -------- Step 5: copy config.json + README to deploy root --------
$sampleConfig = Join-Path $root "config.json"
if (Test-Path -LiteralPath $sampleConfig) {
    Copy-Item -LiteralPath $sampleConfig -Destination $deployDir -Force
    Write-Host "  Copied config.json" -ForegroundColor DarkCyan
}

# -------- Summary --------
Write-Host ""
Write-Host "============================================================"
Write-Host "Publish complete. Deploy directory: $deployDir" -ForegroundColor Green
Write-Host "============================================================"
Write-Host ""
Write-Host "Files in deploy/:" -ForegroundColor Green
Get-ChildItem -LiteralPath $deployDir -File | ForEach-Object {
    $size = "{0,8:N2} MB" -f ($_.Length / 1MB)
    "{0}    {1}" -f $size, $_.Name
} | Write-Host
Write-Host ""
Write-Host "Distribution: copy the entire deploy/ folder to the target server." -ForegroundColor Green
Write-Host "Target server requirements: NONE (.NET runtime is bundled)." -ForegroundColor Green
