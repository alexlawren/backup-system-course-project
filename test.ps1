<#
.SYNOPSIS
    Запуск полного цикла проверок: restore, build, test.
#>
param(
    [switch]$NoRestore,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$RepoRoot = $PSScriptRoot
Set-Location $RepoRoot

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Запуск тестов Системы Резервирования   " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if (-not $NoRestore) {
    Write-Host "1. Восстановление зависимостей (dotnet restore)..." -ForegroundColor Yellow
    dotnet restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not $NoBuild) {
    Write-Host "2. Сборка решения (dotnet build)..." -ForegroundColor Yellow
    dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "3. Запуск тестов (dotnet test)..." -ForegroundColor Yellow
dotnet test --no-build
exit $LASTEXITCODE
