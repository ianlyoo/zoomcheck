<#
.SYNOPSIS
    Builds the ZoomCheck Windows web-dashboard package (installer + portable ZIP).

.DESCRIPTION
    ZoomCheck ships as a single self-contained ASP.NET Core executable
    (ZoomCheck.Backend.exe) that serves the web dashboard and opens the browser
    on start. The Avalonia desktop shell (ZoomCheck.App) is intentionally NOT
    part of the package anymore.

    Outputs:
      dist/windows/package                            publish root (installer payload)
      dist/portable/ZoomCheck-portable-win-x64.zip    portable ZIP
      dist/installer/ZoomCheck-Setup-x64.exe          installer (needs Inno Setup 6)
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$distRoot = Join-Path $repoRoot "dist\windows"
$packageRoot = Join-Path $distRoot "package"
$portableRoot = Join-Path $repoRoot "dist\portable"
$installerRoot = Join-Path $repoRoot "dist\installer"
$backendProject = Join-Path $repoRoot "src\ZoomCheck.Backend\ZoomCheck.Backend.csproj"
$mainExeName = "ZoomCheck.Backend.exe"
$portableZip = Join-Path $portableRoot "ZoomCheck-portable-$($Runtime).zip"

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# ---------------------------------------------------------------------------
# 1. Clean output directories
# ---------------------------------------------------------------------------
Write-Step "Cleaning output directories"
foreach ($dir in @($distRoot, $portableRoot, $installerRoot)) {
    if (Test-Path $dir) {
        Remove-Item $dir -Recurse -Force
    }
}
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
New-Item -ItemType Directory -Force -Path $portableRoot | Out-Null
New-Item -ItemType Directory -Force -Path $installerRoot | Out-Null

# ---------------------------------------------------------------------------
# 2. Publish ZoomCheck.Backend as the single package root
# ---------------------------------------------------------------------------
Write-Step "Publishing ZoomCheck.Backend ($Configuration / $Runtime, self-contained)"
dotnet publish $backendProject -c $Configuration -r $Runtime --self-contained true -p:Version=$Version -p:DebugType=none -p:DebugSymbols=false -o $packageRoot
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

# ---------------------------------------------------------------------------
# 3. Prune development-only artifacts
# ---------------------------------------------------------------------------
Write-Step "Pruning development-only artifacts"
$prunePatterns = @(
    "appsettings.Development.json",
    "*.pdb",
    "*.http",
    "*.xml"
)
foreach ($pattern in $prunePatterns) {
    Get-ChildItem -Path $packageRoot -Filter $pattern -Recurse -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            Write-Host "    removed $($_.FullName.Substring($packageRoot.Length + 1))"
            Remove-Item $_.FullName -Force
        }
}

# ---------------------------------------------------------------------------
# 4. Packaging guard: no desktop shell, no secrets, no DB, no user data
# ---------------------------------------------------------------------------
Write-Step "Verifying package contents"
$problems = New-Object System.Collections.Generic.List[string]

if (-not (Test-Path (Join-Path $packageRoot $mainExeName))) {
    $problems.Add("Missing launch target: $mainExeName is not in the package root.")
}
if (-not (Test-Path (Join-Path $packageRoot "wwwroot\index.html"))) {
    $problems.Add("Missing web dashboard: wwwroot\index.html is not in the package.")
}

# The Avalonia desktop shell must no longer be shipped.
Get-ChildItem -Path $packageRoot -Filter "ZoomCheck.App*" -Recurse -Force -ErrorAction SilentlyContinue |
    ForEach-Object { $problems.Add("Avalonia desktop shell leaked into package: $($_.Name)") }

# Secrets, local state and user data must never be packaged.
$forbiddenFilePatterns = @(
    "*.db", "*.db-shm", "*.db-wal", "*.sqlite", "*.sqlite3",
    "*.env", ".env", ".env.*",
    "*.pfx", "*.p12", "*.pem", "*.key", "*.keystore", "*.jks",
    "secrets.json", "*.secrets.json", "appsettings.Local.json",
    "*.xlsx", "*.xls", "*.csv", "*.log"
)
foreach ($pattern in $forbiddenFilePatterns) {
    Get-ChildItem -Path $packageRoot -Filter $pattern -Recurse -Force -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            $problems.Add("Forbidden file in package ($pattern): $($_.FullName.Substring($packageRoot.Length + 1))")
        }
}

foreach ($forbiddenDir in @("data", "logs", ".git", ".github")) {
    Get-ChildItem -Path $packageRoot -Filter $forbiddenDir -Recurse -Force -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            $problems.Add("Forbidden directory in package: $($_.FullName.Substring($packageRoot.Length + 1))")
        }
}

# Shipped configuration must not carry populated credentials.
$appSettings = Join-Path $packageRoot "appsettings.json"
if (Test-Path $appSettings) {
    $config = Get-Content $appSettings -Raw | ConvertFrom-Json
    if ($config.PSObject.Properties.Name -contains "Zoom") {
        $zoom = $config.Zoom
        foreach ($secretKey in @("WebhookSecretToken", "ClientId", "ClientSecret", "AccountId")) {
            if ($zoom.PSObject.Properties.Name -notcontains $secretKey) {
                continue
            }
            $value = [string]$zoom.PSObject.Properties[$secretKey].Value
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                $problems.Add("appsettings.json ships a populated Zoom secret: Zoom.$secretKey")
            }
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Host ""
    Write-Host "Packaging guard failed:" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    throw "Packaging guard rejected $packageRoot ($($problems.Count) issue(s))."
}

$fileCount = (Get-ChildItem -Path $packageRoot -Recurse -File).Count
Write-Host "    package root OK: $fileCount files, launch target $mainExeName"

# ---------------------------------------------------------------------------
# 5. Portable ZIP
# ---------------------------------------------------------------------------
Write-Step "Creating portable ZIP"
Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $portableZip -CompressionLevel Optimal -Force
Write-Host "    $portableZip"

# ---------------------------------------------------------------------------
# 6. Installer
# ---------------------------------------------------------------------------
if ($SkipInstaller) {
    Write-Step "Skipping installer build (-SkipInstaller)"
    Write-Host "Package root: $packageRoot"
    return
}

$isccRoots = @(
    $env:ProgramFiles,
    ${env:ProgramFiles(x86)}
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

$isccCandidates = @(
    foreach ($root in $isccRoots) {
        Join-Path $root "Inno Setup 6\ISCC.exe"
    }
) | Where-Object { Test-Path $_ }

if (-not $isccCandidates) {
    Write-Step "Inno Setup 6 not found"
    Write-Host "Install it from https://jrsoftware.org/isdl.php to build the installer."
    Write-Host "Portable package is ready at: $packageRoot"
    return
}

Write-Step "Building installer with Inno Setup"
$isccPath = $isccCandidates | Select-Object -First 1
& $isccPath "/DMyAppVersion=$Version" (Join-Path $PSScriptRoot "ZoomCheck.iss")
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  installer : $(Join-Path $installerRoot 'ZoomCheck-Setup-x64.exe')"
Write-Host "  portable  : $portableZip"
