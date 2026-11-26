$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Require-Command([string]$Name, [string]$InstallHint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name tidak ditemukan. $InstallHint"
    }
}

Write-Host "=== ICE BROWSER 1.4.0 release build ===" -ForegroundColor Cyan
Require-Command "dotnet" "Install .NET 8 SDK dari https://dotnet.microsoft.com/download/dotnet/8.0"
$dotnetInfo = dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($dotnetInfo -match '^8\.')) {
    throw ".NET 8 SDK diperlukan. SDK terdeteksi: $dotnetInfo"
}

$publish = Join-Path $root "publish"
$dist = Join-Path $root "dist"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish, $dist | Out-Null

dotnet restore ".\ICEBrowser\ICEBrowser.csproj"
if ($LASTEXITCODE -ne 0) { throw "dotnet restore gagal." }

dotnet publish ".\ICEBrowser\ICEBrowser.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish gagal." }

# Microsoft Edge WebView2 Evergreen Bootstrapper (official Microsoft distribution link)
$webViewBootstrapper = Join-Path $root "Installer\MicrosoftEdgeWebView2Setup.exe"
$webViewUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
Write-Host "Downloading official Microsoft WebView2 Evergreen Bootstrapper..."
Invoke-WebRequest -Uri $webViewUrl -OutFile $webViewBootstrapper -UseBasicParsing
if (-not (Test-Path $webViewBootstrapper) -or (Get-Item $webViewBootstrapper).Length -lt 100000) {
    throw "WebView2 bootstrapper download failed or file is invalid."
}

$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 compiler (ISCC.exe) not found. Install Inno Setup 6."
}

& $iscc ".\Installer\ICEBrowser.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

$installer = Join-Path $dist "ICE-BROWSER-Setup-1.4.0-x64.exe"
if (-not (Test-Path $installer)) { throw "Installer output not found: $installer" }
Write-Host "BUILD SUCCESS: $installer" -ForegroundColor Green
