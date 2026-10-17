<#
.SYNOPSIS
    Запуск демонстрационного окружения с двумя независимыми клиентами (Client A и Client B)
    для проверки двусторонней синхронизации общего набора (SHARED BackupSet).
.PARAMETER NoBrowser
    Не открывать вкладки браузера автоматически.
.PARAMETER WithDocker
    Автоматически запустить PostgreSQL через Docker Compose при недоступности.
#>
param(
    [switch]$NoBrowser,
    [switch]$WithDocker
)

$ErrorActionPreference = "Stop"
$RepoRoot = $PSScriptRoot
Set-Location $RepoRoot

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host " Демонстрационный запуск: ДВА КЛИЕНТА (SHARED)    " -ForegroundColor Cyan
Write-Host " Client A :5100 (data/client-a)                   " -ForegroundColor Cyan
Write-Host " Client B :5200 (data/client-b)                   " -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 1. Проверка dotnet
try {
    $dotnetVersion = dotnet --version
    Write-Host "[OK] .NET SDK: $dotnetVersion" -ForegroundColor Green
} catch {
    Write-Error "Не найден .NET SDK. Установите .NET 9.0 SDK для продолжения."
    exit 1
}

# 2. Проверка PostgreSQL на 127.0.0.1:5432
function Test-TcpConnectionQuick([string]$hostAddress, [int]$port, [int]$timeoutMs = 1500) {
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $ar = $tcp.BeginConnect($hostAddress, $port, $null, $null)
        if ($ar.AsyncWaitHandle.WaitOne($timeoutMs, $false)) {
            $tcp.EndConnect($ar)
            $tcp.Close()
            return $true
        }
        $tcp.Close()
        return $false
    } catch {
        return $false
    }
}

$pgAvailable = Test-TcpConnectionQuick "127.0.0.1" 5432 2000
if (-not $pgAvailable) {
    if ($WithDocker) {
        Write-Host "Попытка запуска PostgreSQL через docker compose..." -ForegroundColor Yellow
        try {
            docker compose up -d postgres
            Start-Sleep -Seconds 3
            $pgAvailable = Test-TcpConnectionQuick "127.0.0.1" 5432 5000
        } catch { }
    }
    if (-not $pgAvailable) {
        Write-Host ""
        Write-Host "[ОШИБКА] PostgreSQL недоступен на 127.0.0.1:5432!" -ForegroundColor Red
        Write-Host "Запустите PostgreSQL: docker compose up -d postgres" -ForegroundColor Yellow
        exit 1
    }
}
Write-Host "[OK] PostgreSQL :5432 доступен" -ForegroundColor Green

# 3. Директории и порты
$RunDir = Join-Path $RepoRoot "data\run"
$LogDir = Join-Path $RepoRoot "data\logs\shared-demo"
$PidFile = Join-Path $RunDir "shared-demo-processes.json"

if (-not (Test-Path $RunDir)) { New-Item -ItemType Directory -Path $RunDir -Force | Out-Null }
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }

$clientADir = Join-Path $RepoRoot "data\client-a"
$clientBDir = Join-Path $RepoRoot "data\client-b"
if (-not (Test-Path $clientADir)) { New-Item -ItemType Directory -Path $clientADir -Force | Out-Null }
if (-not (Test-Path $clientBDir)) { New-Item -ItemType Directory -Path $clientBDir -Force | Out-Null }

function Check-PortInUse([int]$port) {
    try {
        $conns = Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue | Where-Object { $_.State -eq "Listen" }
        if ($conns -ne $null -and $conns.Count -gt 0) { return $true }
    } catch { }
    return $false
}

$ports = @{ 5001 = "MetadataService"; 5002 = "StorageService"; 5000 = "SyncGateway"; 5100 = "Client A"; 5200 = "Client B" }

foreach ($p in $ports.Keys) {
    if (Check-PortInUse $p) {
        Write-Host "[ПРЕДУПРЕЖДЕНИЕ] Порт $p ($($ports[$p])) занят. Остановка через stop-shared-demo.ps1..." -ForegroundColor Yellow
        if (Test-Path (Join-Path $RepoRoot "stop-shared-demo.ps1")) {
            & (Join-Path $RepoRoot "stop-shared-demo.ps1")
            Start-Sleep -Seconds 1
        }
    }
}

# 4. Сборка
Write-Host "Сборка решения..." -ForegroundColor Yellow
dotnet build --verbosity quiet
if ($LASTEXITCODE -ne 0) { Write-Error "Ошибка сборки решения."; exit 1 }

$metaDll = Join-Path $RepoRoot "src\Backup.MetadataService\bin\Debug\net9.0\Backup.MetadataService.dll"
$storageDll = Join-Path $RepoRoot "src\Backup.StorageService\bin\Debug\net9.0\Backup.StorageService.dll"
$gatewayDll = Join-Path $RepoRoot "src\Backup.SyncGateway\bin\Debug\net9.0\Backup.SyncGateway.dll"
$clientDll = Join-Path $RepoRoot "src\Backup.Client.Web\bin\Debug\net9.0\Backup.Client.Web.dll"

function Stop-AllRunningServices {
    foreach ($k in $runningManifest.Keys) {
        $entry = $runningManifest[$k]
        if ($entry -and $entry.Pid -gt 0) {
            try { Stop-Process -Id $entry.Pid -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
}

function Start-ServiceProcess([string]$name, [string]$dllPath, [string]$logName, [string]$customWorkDir = "", [string[]]$extraArgs = @()) {
    $outLog = Join-Path $LogDir ($logName + ".log")
    $errLog = Join-Path $LogDir ($logName + "_err.log")
    if (Test-Path $outLog) { Remove-Item $outLog -Force -ErrorAction SilentlyContinue }
    if (Test-Path $errLog) { Remove-Item $errLog -Force -ErrorAction SilentlyContinue }

    $workDir = if ($customWorkDir) { $customWorkDir } else { Split-Path $dllPath -Parent }

    $allArgs = @($dllPath) + $extraArgs
    $escapedArgs = $allArgs | ForEach-Object {
        if ($_ -match '\s' -or $_ -match '[\(\)]') { '"' + $_ + '"' } else { $_ }
    }
    $argLine = $escapedArgs -join ' '

    $proc = Start-Process -FilePath "dotnet" `
        -ArgumentList $argLine `
        -WorkingDirectory $workDir `
        -RedirectStandardOutput $outLog `
        -RedirectStandardError $errLog `
        -NoNewWindow `
        -PassThru

    return $proc
}

function Wait-PortReady([string]$hostAddress, [int]$port, [System.Diagnostics.Process]$proc, [string]$logName, [int]$timeoutSeconds = 25) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSeconds) {
        if (Test-TcpConnectionQuick $hostAddress $port 300) { return $true }
        if ($proc -ne $null -and $proc.HasExited) {
            Write-Host ""
            Write-Host "[ОШИБКА] Процесс для порта $port завершился (ExitCode: $($proc.ExitCode))!" -ForegroundColor Red
            $logPath = Join-Path $LogDir ($logName + ".log")
            $errPath = Join-Path $LogDir ($logName + "_err.log")
            if (Test-Path $logPath) { Write-Host "Последние строки лога ($logName.log):" -ForegroundColor Yellow; Get-Content $logPath -Tail 15 | Out-Host }
            if (Test-Path $errPath) { Write-Host "Ошибки ($logName" + "_err.log):" -ForegroundColor Yellow; Get-Content $errPath -Tail 15 | Out-Host }
            return $false
        }
        Start-Sleep -Milliseconds 250
    }
    Write-Host "[ТАЙМАУТ] Порт $port не ответил за $timeoutSeconds сек." -ForegroundColor Red
    $logPath = Join-Path $LogDir ($logName + ".log")
    $errPath = Join-Path $LogDir ($logName + "_err.log")
    if (Test-Path $logPath) { Write-Host "Последние строки лога ($logName.log):" -ForegroundColor Yellow; Get-Content $logPath -Tail 15 | Out-Host }
    if (Test-Path $errPath) { Write-Host "Ошибки ($logName" + "_err.log):" -ForegroundColor Yellow; Get-Content $errPath -Tail 15 | Out-Host }
    return $false
}

$runningManifest = @{}

# 5. Запуск бэкенда
Write-Host "Запуск MetadataService..." -ForegroundColor Yellow
$metaProc = Start-ServiceProcess "MetadataService" $metaDll "metadata"
if (-not (Wait-PortReady "127.0.0.1" 5001 $metaProc "metadata")) { Stop-AllRunningServices; exit 1 }
Write-Host "[OK] MetadataService :5001 готов" -ForegroundColor Green
$runningManifest["MetadataService"] = @{ Pid = $metaProc.Id; Port = 5001; ProcessName = "dotnet" }

Write-Host "Запуск StorageService..." -ForegroundColor Yellow
$storageProc = Start-ServiceProcess "StorageService" $storageDll "storage"
if (-not (Wait-PortReady "127.0.0.1" 5002 $storageProc "storage")) { Stop-AllRunningServices; exit 1 }
Write-Host "[OK] StorageService :5002 готов" -ForegroundColor Green
$runningManifest["StorageService"] = @{ Pid = $storageProc.Id; Port = 5002; ProcessName = "dotnet" }

Write-Host "Запуск SyncGateway..." -ForegroundColor Yellow
$gatewayProc = Start-ServiceProcess "SyncGateway" $gatewayDll "gateway"
if (-not (Wait-PortReady "127.0.0.1" 5000 $gatewayProc "gateway")) { Stop-AllRunningServices; exit 1 }
Write-Host "[OK] SyncGateway :5000 готов" -ForegroundColor Green
$runningManifest["SyncGateway"] = @{ Pid = $gatewayProc.Id; Port = 5000; ProcessName = "dotnet" }

# 6. Запуск Client A (:5100) и Client B (:5200)
$clientWorkDir = Join-Path $RepoRoot "src\Backup.Client.Web"

Write-Host "Запуск Client A (:5100)..." -ForegroundColor Yellow
$clientAArgs = @(
    '--urls', 'http://127.0.0.1:5100',
    '--ClientDataRoot', $clientADir,
    '--environment', 'Development',
    '--SharedSyncIntervalSeconds', '3',
    '--MachineName', 'DESKTOP-A',
    '--ClientName', 'Client-A'
)
$clientAProc = Start-ServiceProcess "Client.Web.A" $clientDll "client-a" $clientWorkDir $clientAArgs
if (-not (Wait-PortReady "127.0.0.1" 5100 $clientAProc "client-a")) { Stop-AllRunningServices; exit 1 }
Write-Host "[OK] Client A :5100 готов (данные: data/client-a)" -ForegroundColor Green
$runningManifest["Client.Web.A"] = @{ Pid = $clientAProc.Id; Port = 5100; ProcessName = "dotnet" }

Write-Host "Запуск Client B (:5200)..." -ForegroundColor Yellow
$clientBArgs = @(
    '--urls', 'http://127.0.0.1:5200',
    '--ClientDataRoot', $clientBDir,
    '--environment', 'Development',
    '--SharedSyncIntervalSeconds', '3',
    '--MachineName', 'DESKTOP-B',
    '--ClientName', 'Client-B'
)
$clientBProc = Start-ServiceProcess "Client.Web.B" $clientDll "client-b" $clientWorkDir $clientBArgs
if (-not (Wait-PortReady "127.0.0.1" 5200 $clientBProc "client-b")) { Stop-AllRunningServices; exit 1 }
Write-Host "[OK] Client B :5200 готов (данные: data/client-b)" -ForegroundColor Green
$runningManifest["Client.Web.B"] = @{ Pid = $clientBProc.Id; Port = 5200; ProcessName = "dotnet" }

# 7. Сохранение манифеста
$runningManifest | ConvertTo-Json -Depth 3 | Set-Content -Path $PidFile -Encoding UTF8

Write-Host ""
Write-Host "==================================================" -ForegroundColor Green
Write-Host " Окружение Shared Storage успешно запущено!      " -ForegroundColor Green
Write-Host " Client A: http://127.0.0.1:5100 (Владелец)       " -ForegroundColor White
Write-Host " Client B: http://127.0.0.1:5200 (Участник)       " -ForegroundColor White
Write-Host " Gateway:  127.0.0.1:5000                         " -ForegroundColor White
Write-Host "==================================================" -ForegroundColor Green
Write-Host "Для остановки выполните: .\stop-shared-demo.ps1   " -ForegroundColor Cyan
Write-Host ""

if (-not $NoBrowser) {
    try { Start-Process "http://127.0.0.1:5100" } catch { }
    try { Start-Process "http://127.0.0.1:5200" } catch { }
}

exit 0
