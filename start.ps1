<#
.SYNOPSIS
    Единый запуск всех сервисов системы резервного копирования одной командой.
.PARAMETER NoBrowser
    Не открывать браузер автоматически после успешного запуска.
.PARAMETER WithDocker
    Автоматически попытаться запустить PostgreSQL в Docker при его недоступности.
#>
param(
    [switch]$NoBrowser,
    [switch]$WithDocker
)

$ErrorActionPreference = "Stop"
$RepoRoot = $PSScriptRoot
Set-Location $RepoRoot

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Запуск Системы Резервного Копирования  " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

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
        Write-Host ""
        Write-Host "Для работы системы требуется запущенная СУБД PostgreSQL." -ForegroundColor Yellow
        Write-Host "Варианты решения:" -ForegroundColor Yellow
        Write-Host "  1. Запустите PostgreSQL в Docker:" -ForegroundColor White
        Write-Host "     docker compose up -d postgres" -ForegroundColor Cyan
        Write-Host "  2. Или запустите локальную службу PostgreSQL на порту 5432." -ForegroundColor White
        Write-Host "  3. Или запустите скрипт с параметром -WithDocker:" -ForegroundColor White
        Write-Host "     .\start.ps1 -WithDocker" -ForegroundColor Cyan
        exit 1
    }
}
Write-Host "[OK] PostgreSQL :5432 доступен" -ForegroundColor Green

# 3. Проверка портов
$RunDir = Join-Path $RepoRoot "data\run"
$LogDir = Join-Path $RepoRoot "data\logs\current"
$PidFile = Join-Path $RunDir "processes.json"

if (-not (Test-Path $RunDir)) { New-Item -ItemType Directory -Path $RunDir -Force | Out-Null }
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }

function Check-PortInUse([int]$port) {
    try {
        $conns = Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue | Where-Object { $_.State -eq "Listen" }
        if ($conns -ne $null -and $conns.Count -gt 0) { return $true }
    } catch { }
    return $false
}

$ports = @{ 5001 = "Backup.MetadataService"; 5002 = "Backup.StorageService"; 5000 = "Backup.SyncGateway"; 5100 = "Backup.Client.Web" }

foreach ($p in $ports.Keys) {
    if (Check-PortInUse $p) {
        Write-Host "[ПРЕДУПРЕЖДЕНИЕ] Порт $p ($($ports[$p])) занят. Остановка через stop.ps1..." -ForegroundColor Yellow
        if (Test-Path (Join-Path $RepoRoot "stop.ps1")) {
            & (Join-Path $RepoRoot "stop.ps1")
            Start-Sleep -Seconds 1
        }
        if (Check-PortInUse $p) {
            Write-Host "[ОШИБКА] Порт $p по-прежнему занят сторонним процессом." -ForegroundColor Red
            exit 1
        }
    }
}

# 4. Сборка
$metaDll = Join-Path $RepoRoot "src\Backup.MetadataService\bin\Debug\net9.0\Backup.MetadataService.dll"
$storageDll = Join-Path $RepoRoot "src\Backup.StorageService\bin\Debug\net9.0\Backup.StorageService.dll"
$gatewayDll = Join-Path $RepoRoot "src\Backup.SyncGateway\bin\Debug\net9.0\Backup.SyncGateway.dll"
$clientDll = Join-Path $RepoRoot "src\Backup.Client.Web\bin\Debug\net9.0\Backup.Client.Web.dll"

if (-not (Test-Path $metaDll) -or -not (Test-Path $storageDll) -or -not (Test-Path $gatewayDll) -or -not (Test-Path $clientDll)) {
    Write-Host "Сборка проектов решения..." -ForegroundColor Yellow
    dotnet build --verbosity quiet
    if ($LASTEXITCODE -ne 0) { Write-Error "Ошибка сборки решения."; exit 1 }
}

# 5. Запуск процесса
function Start-ServiceProcess([string]$name, [string]$dllPath, [string]$logName, [string]$customWorkDir = "", [string]$extraArgs = "") {
    $outLog = Join-Path $LogDir ($logName + ".log")
    $errLog = Join-Path $LogDir ($logName + "_err.log")
    if (Test-Path $outLog) { Remove-Item $outLog -Force -ErrorAction SilentlyContinue }
    if (Test-Path $errLog) { Remove-Item $errLog -Force -ErrorAction SilentlyContinue }

    $workDir = if ($customWorkDir) { $customWorkDir } else { Split-Path $dllPath -Parent }
    $dllFile = if ($customWorkDir) { "bin\Debug\net9.0\" + (Split-Path $dllPath -Leaf) } else { Split-Path $dllPath -Leaf }

    $argList = @($dllFile)
    if ($extraArgs) {
        $argList += ($extraArgs -split ' ')
    }

    $proc = Start-Process -FilePath "dotnet" `
        -ArgumentList $argList `
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
            if (Test-Path $logPath) { Write-Host "Последние строки лога ($logName.log):" -ForegroundColor Yellow; Get-Content $logPath -Tail 15 }
            if (Test-Path $errPath) { Write-Host "Ошибки ($logName" + "_err.log):" -ForegroundColor Yellow; Get-Content $errPath -Tail 15 }
            return $false
        }
        Start-Sleep -Milliseconds 250
    }
    Write-Host "[ТАЙМАУТ] Порт $port не ответил за $timeoutSeconds сек." -ForegroundColor Red
    return $false
}

$runningManifest = @{}

# 6. Запуск сервисов
Write-Host "Запуск MetadataService..." -ForegroundColor Yellow
$metaProc = Start-ServiceProcess "MetadataService" $metaDll "metadata"
if (-not (Wait-PortReady "127.0.0.1" 5001 $metaProc "metadata")) { try { $metaProc.Kill() } catch { }; exit 1 }
Write-Host "[OK] MetadataService :5001 готов" -ForegroundColor Green
$runningManifest["MetadataService"] = @{ Pid = $metaProc.Id; Port = 5001; ProcessName = "dotnet" }

Write-Host "Запуск StorageService..." -ForegroundColor Yellow
$storageProc = Start-ServiceProcess "StorageService" $storageDll "storage"
if (-not (Wait-PortReady "127.0.0.1" 5002 $storageProc "storage")) { try { $metaProc.Kill() } catch { }; try { $storageProc.Kill() } catch { }; exit 1 }
Write-Host "[OK] StorageService :5002 готов" -ForegroundColor Green
$runningManifest["StorageService"] = @{ Pid = $storageProc.Id; Port = 5002; ProcessName = "dotnet" }

Write-Host "Запуск SyncGateway..." -ForegroundColor Yellow
$gatewayProc = Start-ServiceProcess "SyncGateway" $gatewayDll "gateway"
if (-not (Wait-PortReady "127.0.0.1" 5000 $gatewayProc "gateway")) { try { $metaProc.Kill() } catch { }; try { $storageProc.Kill() } catch { }; try { $gatewayProc.Kill() } catch { }; exit 1 }
Write-Host "[OK] SyncGateway :5000 готов" -ForegroundColor Green
$runningManifest["SyncGateway"] = @{ Pid = $gatewayProc.Id; Port = 5000; ProcessName = "dotnet" }

Write-Host "Запуск Client.Web..." -ForegroundColor Yellow
$clientWorkDir = Join-Path $RepoRoot "src\Backup.Client.Web"
$clientProc = Start-ServiceProcess "Client.Web" $clientDll "client" $clientWorkDir
if (-not (Wait-PortReady "127.0.0.1" 5100 $clientProc "client" 25)) { try { $metaProc.Kill() } catch { }; try { $storageProc.Kill() } catch { }; try { $gatewayProc.Kill() } catch { }; try { $clientProc.Kill() } catch { }; exit 1 }
Write-Host "[OK] Client.Web :5100 готов" -ForegroundColor Green
$runningManifest["Client.Web"] = @{ Pid = $clientProc.Id; Port = 5100; ProcessName = "dotnet" }

# 7. Манифест
$runningManifest | ConvertTo-Json -Depth 3 | Set-Content -Path $PidFile -Encoding UTF8

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "Система резервного копирования запущена " -ForegroundColor Green
Write-Host "Web UI: http://localhost:5100           " -ForegroundColor White
Write-Host "Gateway TCP: 127.0.0.1:5000             " -ForegroundColor White
Write-Host "Metadata TCP: 127.0.0.1:5001            " -ForegroundColor White
Write-Host "Storage TCP: 127.0.0.1:5002             " -ForegroundColor White
Write-Host "========================================" -ForegroundColor Green
Write-Host "Для остановки всех сервисов выполните: .\stop.ps1" -ForegroundColor Cyan
Write-Host ""

if (-not $NoBrowser) {
    try { Start-Process "http://localhost:5100" } catch { }
}

exit 0
