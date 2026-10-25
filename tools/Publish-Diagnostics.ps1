[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $repository 'artifacts'
$staging = Join-Path $artifacts ('diagnostics-stage-' + [Guid]::NewGuid().ToString('N'))
$buildRoot = Join-Path $artifacts ('diagnostics-build-' + [Guid]::NewGuid().ToString('N'))
$projectRoot = Join-Path $repository 'dotnet\tools\Atsm.Diagnostics.Maui'
$binRoot = Join-Path $projectRoot 'bin'
$publishBase = Join-Path $binRoot 'Release\net10.0-windows10.0.19041.0\win-x64'
$destination = Join-Path $publishBase 'publish'
$project = Join-Path $projectRoot 'Atsm.Diagnostics.Maui.csproj'
$cli = Join-Path $repository 'dotnet\tools\Atsm.Tools.Trace\Atsm.Tools.Trace.csproj'
function Confirm-PublicationPath([string] $path, [string] $ownerRoot) {
    $resolved = [IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith($ownerRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publication path is outside its owning directory.' }
    $ancestor = $resolved
    while ($ancestor.Length -gt $repository.Length) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Publication path traverses a reparse point.' }
        $ancestor = Split-Path -Parent $ancestor
    }
}
function Remove-TemporaryBuild([string] $path) {
    Confirm-PublicationPath $path $artifacts
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
Confirm-PublicationPath $staging $artifacts
Confirm-PublicationPath $buildRoot $artifacts
Confirm-PublicationPath $destination $binRoot
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Push-Location $repository
try {
    # A clean candidate prevents stale plugin references. Only the current MAUI publication is kept.
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:AtsmRequireStrongName=true --artifacts-path $buildRoot -o $staging --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'MAUI publication failed; current package untouched.' }
    $unusedLanguages = @(Get-ChildItem -LiteralPath $staging -Recurse -File -Filter '*.mui' | Where-Object {
        [IO.Path]::GetRelativePath($staging, $_.FullName) -notmatch '^(es(-AR|-ES)?|en-US)[\\/]'
    })
    if ($unusedLanguages.Count -ne 0) { throw 'Publication contains unrequested WinUI languages.' }
    & dotnet build $cli -c Release -p:AtsmRequireStrongName=true --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Publication verifier build failed.' }
    & dotnet (Join-Path $repository 'dotnet\tools\Atsm.Tools.Trace\bin\Release\net10.0\atsm-trace.dll') verify-publication $staging
    if ($LASTEXITCODE -ne 0) { throw 'Publication contains missing ATSM dependencies.' }
    foreach ($required in @('atsm-diagnostics.exe', 'atsm-diagnostics.dll', 'Atsm.Plugin.Legacy.Testing.dll', 'coreclr.dll', 'Microsoft.UI.Xaml.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $required))) { throw "Publication component missing: $required" }
    }
    $resultFile = Join-Path $staging 'publication-self-test.txt'
    $process = Start-Process -FilePath (Join-Path $staging 'atsm-diagnostics.exe') -ArgumentList @('--self-test', ('"' + $resultFile + '"')) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'MAUI startup self-test exceeded 30 s.' }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $resultFile) -or -not (Get-Content -LiteralPath $resultFile -Raw).StartsWith('PASS:')) { throw 'MAUI startup/custody self-test failed.' }
    Write-Output (Get-Content -LiteralPath $resultFile -Raw)
    $manifest = Get-ChildItem -LiteralPath $staging -Recurse -File | Sort-Object FullName | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetRelativePath($staging, $_.FullName)
    }
    $manifest | Set-Content -LiteralPath (Join-Path $staging 'SHA256SUMS.txt') -Encoding utf8
    New-Item -ItemType Directory -Path $publishBase -Force | Out-Null
    if (Test-Path -LiteralPath $destination) {
        # A generated output may be replaced; it is never an archive of user cases.
        $userData = @(Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object { $_.Extension -in @('.atsmcase','.pfx','.p12','.log','.csv','.rdml','.rdm','.db','.sqlite','.sqlite3') })
        if ($userData.Count -ne 0) { throw 'Publication folder contains user data; keep it outside generated outputs before publishing.' }
        Confirm-PublicationPath $destination $binRoot
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    Move-Item -LiteralPath $staging -Destination $destination
    $zip = Join-Path $publishBase 'ATSM-Diagnostics-MAUI-Windows-x64.zip'
    Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $zip -Force
    Write-Output "Executable: $destination\atsm-diagnostics.exe"
    Get-FileHash -LiteralPath $zip -Algorithm SHA256
}
finally {
    Pop-Location
    Remove-TemporaryBuild $staging
    Remove-TemporaryBuild $buildRoot
}
