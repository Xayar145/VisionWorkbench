param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot 'src\VisionWorkbench\VisionWorkbench.csproj'
if (-not $env:HALCONROOT -or -not (Test-Path -LiteralPath (Join-Path $env:HALCONROOT 'bin\dotnet35\halcondotnet.dll'))) {
    throw '请先安装 HALCON 24.11，并将 HALCONROOT 设置为实际安装目录。'
}

& dotnet build $projectPath -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "构建失败，退出码 $LASTEXITCODE" }
$applicationPath = Join-Path $projectRoot "src\VisionWorkbench\bin\$Configuration\net10.0-windows\VisionWorkbench.exe"
Start-Process -FilePath $applicationPath -WorkingDirectory $projectRoot | Out-Null
