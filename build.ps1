# Build script for MinioSync
# Builds both projects and copies outputs to a common deploy directory

$msbuild = "C:\Program Files (x86)\MSBuild\14.0\Bin\MSBuild.exe"
$solution = "$PSScriptRoot\MinioSync.sln"
$deployDir = "$PSScriptRoot\deploy"

Write-Host "=== Building MinioSync ==="

# Build the solution
& $msbuild $solution /p:Configuration=Debug /t:Rebuild /v:q 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build FAILED with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "Build SUCCESS" -ForegroundColor Green

# Create deploy directory
if (Test-Path $deployDir) {
    Remove-Item -Path "$deployDir\*" -Recurse -Force
}
New-Item -ItemType Directory -Path $deployDir -Force | Out-Null

# Copy binaries
Write-Host "Copying to $deployDir ..."
Copy-Item "$PSScriptRoot\MinioSync\bin\Debug\MinioSync.exe" $deployDir
Copy-Item "$PSScriptRoot\MinioSync\bin\Debug\MinioSync.pdb" $deployDir
Copy-Item "$PSScriptRoot\SyncWorker\bin\Debug\SyncWorker.exe" $deployDir
Copy-Item "$PSScriptRoot\SyncWorker\bin\Debug\SyncWorker.pdb" $deployDir
Copy-Item "$PSScriptRoot\FullSync\bin\Debug\FullSync.exe" $deployDir
Copy-Item "$PSScriptRoot\FullSync\bin\Debug\FullSync.pdb" $deployDir
Copy-Item "$PSScriptRoot\MinioCommon\bin\Debug\MinioCommon.dll" $deployDir
Copy-Item "$PSScriptRoot\MinioCommon\bin\Debug\MinioCommon.pdb" $deployDir

# Copy configs
$deployConfigs = "$deployDir\configs"
if (-not (Test-Path $deployConfigs)) {
    New-Item -ItemType Directory -Path $deployConfigs -Force | Out-Null
}
Copy-Item "$PSScriptRoot\configs\*.json" $deployConfigs

Write-Host "=== Deploy ready at: $deployDir ===" -ForegroundColor Green
Write-Host "Files:" -ForegroundColor Yellow
Get-ChildItem $deployDir -Recurse | Select-Object Mode, LastWriteTime, Length, FullName | Format-Table -AutoSize
