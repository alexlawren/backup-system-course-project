<#
.SYNOPSIS
    Корректная остановка сервисов системы резервного копирования, запущенных через start.ps1.
#>

$RepoRoot = $PSScriptRoot
Set-Location $RepoRoot

$RunDir = Join-Path $RepoRoot "data\run"
$PidFile = Join-Path $RunDir "processes.json"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Остановка Системы Резервного Копирования" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if (-not (Test-Path $PidFile)) {
    Write-Host "Файл манифеста ($PidFile) не найден. Попытка поиска по портам..." -ForegroundColor Yellow
    $ports = @(5100, 5000, 5002, 5001)
    $stoppedCount = 0
    foreach ($p in $ports) {
        try {
            $conns = Get-NetTCPConnection -LocalPort $p -ErrorAction SilentlyContinue | Where-Object { $_.State -eq "Listen" }
            foreach ($c in $conns) {
                if ($c.OwningProcess -gt 0) {
                    $proc = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
                    if ($proc -ne $null -and ($proc.ProcessName -match "dotnet|Backup")) {
                        Write-Host "Остановка процесса $($proc.Name) (PID: $($proc.Id)) на порту $p..." -ForegroundColor Yellow
                        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                        $stoppedCount++
                    }
                }
            }
        } catch { }
    }
    Write-Host "Остановлено процессов: $stoppedCount" -ForegroundColor Green
    exit 0
}

try {
    $json = Get-Content $PidFile -Raw | ConvertFrom-Json
} catch {
    Write-Host "Не удалось прочитать манифест $PidFile." -ForegroundColor Red
    Remove-Item -Path $PidFile -Force -ErrorAction SilentlyContinue
    exit 1
}

$services = @("Client.Web", "SyncGateway", "StorageService", "MetadataService")

foreach ($s in $services) {
    if ($json.PSObject.Properties[$s]) {
        $entry = $json.$s
        $pidToStop = $entry.Pid
        $port = $entry.Port

        if ($pidToStop -and $pidToStop -gt 0) {
            $proc = Get-Process -Id $pidToStop -ErrorAction SilentlyContinue
            if ($proc -ne $null) {
                Write-Host "Остановка $s (PID: $pidToStop, Порт: $port)..." -ForegroundColor Yellow
                try {
                    $proc.Kill($true)
                    $proc.WaitForExit(3000)
                    Write-Host "[OK] $s остановлен" -ForegroundColor Green
                } catch {
                    try { Stop-Process -Id $pidToStop -Force -ErrorAction SilentlyContinue } catch { }
                    Write-Host "[OK] $s принудительно завершен" -ForegroundColor Green
                }
            } else {
                Write-Host "[ИНФО] $s (PID: $pidToStop) уже не запущен." -ForegroundColor DarkGray
            }
        }
    }
}

Remove-Item -Path $PidFile -Force -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "Все сервисы системы резервного копирования успешно остановлены." -ForegroundColor Green
Write-Host "Манифест $PidFile очищен." -ForegroundColor DarkGray
Write-Host "База данных PostgreSQL осталась нетронутой." -ForegroundColor DarkGray
