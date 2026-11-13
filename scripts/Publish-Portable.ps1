param(
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'artifacts'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

# Use a new staging directory on each run so previous settings or stale DLLs cannot enter the ZIP.
$staging = Join-Path $OutputDirectory ('WorkTree-win-x64-' + [Guid]::NewGuid().ToString('N'))
$project = Join-Path $projectRoot 'src\WorkTree.Desktop\WorkTree.Desktop.csproj'
dotnet publish $project -c Release -r win-x64 --self-contained false -p:DebugType=None -o $staging
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE)."
}

Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $staging
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $staging -Recurse
$zip = Join-Path $OutputDirectory 'WorkTree-win-x64.zip'
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -Force
Write-Output $zip
