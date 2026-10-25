[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $repository 'artifacts'
$staging = Join-Path $artifacts ('diagnostics-stage-' + [Guid]::NewGuid().ToString('N'))
$buildRoot = Join-Path $artifacts ('diagnostics-build-' + [Guid]::NewGuid().ToString('N'))
$destination = Join-Path $artifacts 'diagnostics\windows-x64'
$project = Join-Path $repository 'dotnet\tools\Atsm.Diagnostics.Maui\Atsm.Diagnostics.Maui.csproj'
$cli = Join-Path $repository 'dotnet\tools\Atsm.Tools.Trace\Atsm.Tools.Trace.csproj'
function Confirm-ArtifactPath([string] $path) {
    $resolved = [IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publication path is outside artifacts.' }
}
Confirm-ArtifactPath $staging
Confirm-ArtifactPath $buildRoot
Confirm-ArtifactPath $destination
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Push-Location $repository
try {
    # A new directory prevents stale DLLs/deps.json after a plugin rename. Preserve the previous
    # working publication until the candidate has passed dependency and real-window self-tests.
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:AtsmRequireStrongName=true --artifacts-path $buildRoot -o $staging --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'MAUI publication failed; previous package preserved.' }
    $unusedLanguages = @(Get-ChildItem -LiteralPath $staging -Recurse -File -Filter '*.mui' | Where-Object {
        [IO.Path]::GetRelativePath($staging, $_.FullName) -notmatch '^(es(-AR|-ES)?|en-US)[\\/]'
    })
    if ($unusedLanguages.Count -ne 0) { throw 'Publication contains unrequested WinUI languages; previous package preserved.' }
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
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    if (Test-Path -LiteralPath $destination) {
        $previous = Join-Path $artifacts ('diagnostics-previous-' + [Guid]::NewGuid().ToString('N'))
        Confirm-ArtifactPath $previous
        Move-Item -LiteralPath $destination -Destination $previous
    }
    Move-Item -LiteralPath $staging -Destination $destination
    # Keep the executable link from the earlier delivery useful. Archive that complete directory
    # first (including any user-added files) and replace it with the verified MAUI publication.
    $legacy = [IO.Path]::GetFullPath((Join-Path $repository 'dotnet\tools\Atsm.Diagnostics.Windows\bin\Release\net10.0-windows\publish'))
    $expectedLegacy = $repository + '\dotnet\tools\Atsm.Diagnostics.Windows\bin\Release\net10.0-windows\publish'
    if ($legacy -ne $expectedLegacy) { throw 'Unexpected legacy publication target.' }
    if (Test-Path -LiteralPath $legacy) {
        $retired = Join-Path $artifacts ('diagnostics-retired-wpf-' + [Guid]::NewGuid().ToString('N'))
        Confirm-ArtifactPath $retired
        Move-Item -LiteralPath $legacy -Destination $retired
    }
    New-Item -ItemType Directory -Path $legacy -Force | Out-Null
    Copy-Item -Path (Join-Path $destination '*') -Destination $legacy -Recurse
    $zip = Join-Path $artifacts 'ATSM-Diagnostics-MAUI-Windows-x64.zip'
    Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $zip -Force
    $legacyZip = Join-Path (Split-Path -Parent $legacy) 'ATSM-Diagnostics-Windows.zip'
    if (Test-Path -LiteralPath $legacyZip) {
        $retiredZip = Join-Path $artifacts ('diagnostics-retired-wpf-' + [Guid]::NewGuid().ToString('N') + '.zip')
        Confirm-ArtifactPath $retiredZip
        Move-Item -LiteralPath $legacyZip -Destination $retiredZip
    }
    Copy-Item -LiteralPath $zip -Destination $legacyZip
    Write-Output "Executable: $destination\atsm-diagnostics.exe"
    Get-FileHash -LiteralPath $zip -Algorithm SHA256
}
finally { Pop-Location }
