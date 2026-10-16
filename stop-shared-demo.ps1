<#
.SYNOPSIS
    Остановка сервисов демонстрационного окружения с двумя клиентами.
#>

$RepoRoot = $PSScriptRoot
Set-Location $RepoRoot

$RunDir = Join-Path $RepoRoot "data\run"
$PidFile = Join-Path $RunDir "shared-demo-processes.json"

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host " Остановка Shared Demo (Client A + B + Сервисы)   " -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

if (Test-Path $PidFile) {
    try {
        $json = Get-Content $PidFile -Raw | ConvertFrom-Json
        $services = @("Client.Web.B", "Client.Web.A", "SyncGateway", "StorageService", "MetadataService")
        foreach ($s in $services) {
            if ($json.PSObject.Properties[$s]) {
                $entry = $json.$s
                $pidToStop = $entry.Pid
                if ($pidToStop -and $pidToStop -gt 0) {
                    $proc = Get-Process -Id $pidToStop -ErrorAction SilentlyContinue
                    if ($proc -ne $null) {
                        Write-Host "Остановка $s (PID: $pidToStop)..." -ForegroundColor Yellow
                        try {
                            $proc.Kill($true)
                            $proc.WaitForExit(3000)
                            Write-Host "[OK] $s остановлен" -ForegroundColor Green
                        } catch {
                            try { Stop-Process -Id $pidToStop -Force -ErrorAction SilentlyContinue } catch { }
                        }
                    }
                }
            }
        }
    } catch { }
    Remove-Item -Path $PidFile -Force -ErrorAction SilentlyContinue
}

# Дополнительная очистка портов demo
$ports = @(5100, 5200, 5000, 5002, 5001)
foreach ($p in $ports) {
    try {
        $conns = Get-NetTCPConnection -LocalPort $p -ErrorAction SilentlyContinue | Where-Object { $_.State -eq "Listen" }
        foreach ($c in $conns) {
            if ($c.OwningProcess -gt 0) {
                $proc = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
                if ($proc -ne $null -and ($proc.ProcessName -match "dotnet|Backup")) {
                    Write-Host "Принудительная остановка $($proc.Name) (PID: $($proc.Id)) на порту $p..." -ForegroundColor Yellow
                    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                }
            }
        }
    } catch { }
}

Write-Host ""
Write-Host "[OK] Все сервисы демонстрационного окружения остановлены." -ForegroundColor Green
exit 0
