param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$distRoot = Join-Path $repoRoot "dist\windows"
$appPublish = Join-Path $distRoot "app"
$backendPublish = Join-Path $distRoot "backend"
$packageRoot = Join-Path $distRoot "package"
$backendTarget = Join-Path $packageRoot "Backend"

Remove-Item $distRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $appPublish | Out-Null
New-Item -ItemType Directory -Force -Path $backendPublish | Out-Null
New-Item -ItemType Directory -Force -Path $backendTarget | Out-Null

dotnet publish "$repoRoot\src\ZoomCheck.App\ZoomCheck.App.csproj" -c $Configuration -r $Runtime --self-contained true -o $appPublish
dotnet publish "$repoRoot\src\ZoomCheck.Backend\ZoomCheck.Backend.csproj" -c $Configuration -r $Runtime --self-contained true -o $backendPublish

Copy-Item "$appPublish\*" $packageRoot -Recurse -Force
Copy-Item "$backendPublish\*" $backendTarget -Recurse -Force

$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    Write-Host "Inno Setup not found. Install Inno Setup 6 from https://jrsoftware.org/isdl.php"
    Write-Host "Published self-contained package is ready at: $packageRoot"
    exit 0
}

& $iscc "/DMyAppVersion=$Version" "$PSScriptRoot\ZoomCheck.iss"
