# Клиент-серверная система резервного копирования файлов

Курсовой проект по дисциплине «Программирование сетевых приложений».

---

## БЫСТРЫЙ ЗАПУСК (One-Command Startup)

Запуск всей системы **одной командой** из корня репозитория:

```powershell
.\start.ps1
```

После успешного запуска автоматически откроется русскоязычный веб-интерфейс:
👉 **http://localhost:5100**

Сетевые порты сервисов:
- **Web UI:** `http://localhost:5100`
- **Sync Gateway (TCP):** `127.0.0.1:5000`
- **Metadata Service (TCP):** `127.0.0.1:5001`
- **Storage Service (TCP):** `127.0.0.1:5002`

Остановка всех запущенных сервисов:
```powershell
.\stop.ps1
```

Запуск полного набора автоматических тестов (212 тестов):
```powershell
.\test.ps1
```

> [!NOTE]
> **Архитектурный инвариант:** Скрипт `start.ps1` **НЕ объединяет** микросервисы в монолит и не меняет Architecture v1.1. В операционной системе запускаются строго независимые процессы (`MetadataService`, `StorageService`, `SyncGateway`, `Client.Web`). Скрипт автоматизирует предварительную проверку PostgreSQL (:5432), сборку, опрос готовности портов и ведение логов в `data/logs/current/`.

---

**SHARED STORAGE CHANGE-SET #1:** SHARED BACKUP SET MEMBERSHIP FOUNDATION + 212 TESTS GREEN
- Расширение модели наборов резервного копирования: режим доступа `AccessMode` (`PRIVATE` / `SHARED`), защищенный криптографический код доступа `ShareCode` (31 однозначный символ, формат `XXXX-XXXX`);
- Новая реляционная сущность `backup_set_members`: связь рабочих станций и наборов, роли `OWNER` и `MEMBER`, миграция БД EF Core с автоматическим бэкфиллом существующих наборов;
- Инвариант пространства имен хранилища: физические блобы версий всегда сохраняются в пространстве имен владельца набора (`clients/{OwnerClientId}/backup-sets/{backupSetId}/...`), предотвращая фрагментацию файлов между участниками;
- Аудит авторства в журнале изменений: атрибут `ActorClientId` и навигация `ActorClient` для точной фиксации рабочей станции, выполнившей каждую операцию;
- Серверный контроль доступа: метод `EnsureBackupSetAccessAsync` гарантирует защиту от несанкционированного доступа неавторизованных клиентов (`ACCESS_DENIED`);
- Новые операции Protocol v1: `JoinSharedBackupSet` и `GetBackupSetMembers` с полной сквозной поддержкой в `MetadataService`, `SyncGateway` и клиенте;
- Добавлен комплексный тестовый сьют `SharedBackupSetTests` (юнит-, интеграционные и сквозной тест `SharedSetFoundation_E2E`), подтверждена 100% обратная совместимость всех 197 существующих тестов (всего 212 тестов в зеленой зоне).

---

**RELEASE FIX #2:** BLAZOR STATIC ASSETS / CSS SCOPED DELIVERY + VISUAL BROWSER VERIFICATION + 184 TESTS GREEN
- Устранен runtime bug отдачи статических стилей Blazor (.NET 9) `Backup.Client.Web.*.styles.css` (HTTP 500 FileNotFoundException): включена явная загрузка `StaticWebAssetsLoader.UseStaticWebAssets` в конвейере хоста и передача `--environment Development` в скрипте запуска `start.ps1`;
- Подтверждено отображение темной темы, карточек, цветных статусов, адаптивной навигации и русской локализации в браузере;
- Добавлен автоматический регрессионный сьют `BlazorStaticAssetsRegressionTests` (184 теста в зеленой зоне);
- Полное сохранение стабильности TCP Protocol v1 и one-command startup (`.\start.ps1`).

---

**RELEASE FIX #1:** TCP SESSION/FRAMING STABILIZATION + FULL RUSSIAN UI + ONE-COMMAND STARTUP
- Устранен runtime bug рассинхронизации TCP framing (`HeaderLength 2065855593 bytes`): вычитка тела `Pong`, атомарная очистка сокетов при разрыве, строгая сериализация сессии через `SemaphoreSlim(1,1)`;
- Полная русификация всех страниц Web UI (`/`, `/files`, `/files/{id}`, `/history`, интерактивные диалоги, панель «Быстрый старт»);
- Автоматизированный запуск и остановка всей системы (`start.ps1`, `stop.ps1`, `test.ps1`);
- 181 автоматический тест в зеленой зоне (включая регрессионный сьют `TcpSessionFramingRegressionTests` и интерактивный сквозной `LiveSmokeVerificationTests`).

---

## 1. Обзор проекта (Project Overview)

Система представляет собой кроссплатформенное распределенное клиент-серверное решение для автоматического инкрементального резервного копирования файлов с контролем версий, защитой от потери несохраненных правок, ведением аудиторского журнала и возможностью восстановления любой исторической версии обратно в локальную рабочую директорию.

Ключевые свойства:
- **Собственный сетевой протокол:** бинарный потоковый TCP Protocol v1 поверх сырых сокетов (`System.Net.Sockets`) без использования HTTP/REST/gRPC между сервисами;
- **Надежное двухфазное версионирование:** гарантированное сохранение физического блоба в хранилище ДО фиксации метаданных в транзакции PostgreSQL;
- **Неизменяемость версий (Version Immutability):** старые ревизии никогда не перезаписываются; операция восстановления создает новую ревизию с сохранением всей цепочки истории;
- **Константный расход памяти:** потоковая ретрансляция данных через Gateway с буфером 64 KiB без промежуточных файлов на диске шлюза и без вычитки файлов целиком в оперативную память;
- **Автоматическая синхронизация:** интеллектуальный `FileSystemWatcher` с дебаунсом 750 мс, подавлением эхо-событий восстановления и начальной реконсиляцией манифеста.

---

## 2. Архитектура системы (Architecture v1.1)

```
Browser (Пользователь)
    |
    | HTTP / Blazor (http://localhost:5100)
    v
Backup.Client.Web (Локальный UI клиента + FileSystemWatcher + SyncEngine)
    |
    | TCP (127.0.0.1:5000) [Protocol v1]
    v
Backup.SyncGateway (Внешний шлюз синхронизации и оркестратор)
    |
    +---- TCP (127.0.0.1:5001) [Protocol v1] ----> Backup.MetadataService
    |                                                |
    |                                                | EF Core 9 / Npgsql
    |                                                v
    |                                              PostgreSQL (backup_system)
    |
    +---- TCP (127.0.0.1:5002) [Protocol v1] ----> Backup.StorageService
                                                     |
                                                     v
                                               File Storage (data/storage)
```

Архитектурные инварианты:
1. База данных PostgreSQL доступна **исключительно** сервису `Backup.MetadataService`.
2. Файловое хранилище доступно **исключительно** сервису `Backup.StorageService`.
3. Клиент взаимодействует **исключительно** со шлюзом `Backup.SyncGateway`.
4. Шлюз оркеструет запросы, не имея прямого доступа к базе данных или файловому каталогу хранилища.

---

## 3. Системные требования (Requirements)

- **ОС:** Windows 10/11, Linux (Ubuntu 22.04+), macOS.
- **Среда выполнения:** .NET 9 SDK (целевая платформа `net9.0`).
- **EF Core CLI:** `dotnet tool install --global dotnet-ef` (версия 9.x).
- **СУБД:** PostgreSQL 17 (нативная служба или контейнер Docker).

---

## 4. Конфигурация сервисов (Configuration)

Все сетевые хосты, порты и пути к данным настраиваются в файлах `appsettings.json` соответствующих проектов:

| Сервис | Файл конфигурации | Параметры по умолчанию |
| :--- | :--- | :--- |
| **Backup.MetadataService** | `src/Backup.MetadataService/appsettings.json` | Порт: `5001`, ConnectionString: `Host=127.0.0.1;Port=5432;Database=backup_system;Username=backup_app;Password=...` |
| **Backup.StorageService** | `src/Backup.StorageService/appsettings.json` | Порт: `5002`, RootPath: `data/storage` |
| **Backup.SyncGateway** | `src/Backup.SyncGateway/appsettings.json` | Порт: `5000`, MetadataHost: `127.0.0.1:5001`, StorageHost: `127.0.0.1:5002` |
| **Backup.Client.Web** | `src/Backup.Client.Web/appsettings.json` | URL: `http://localhost:5100`, GatewayHost: `127.0.0.1:5000`, DataRoot: `data/client` |

---

## 5. PostgreSQL и Docker (PostgreSQL / Docker)

### Вариант А: Использование Docker Compose
1. Убедитесь, что демон Docker запущен.
2. Запустите контейнер PostgreSQL:
   ```powershell
   docker compose up -d
   ```
3. Проверьте статус контейнера:
   ```powershell
   docker compose ps
   ```

> [!NOTE]
> **Docker verification audit:** В среде разработки Windows при неактивном пайпе демона Docker Desktop верификация выполнена с отметкой:
> `Docker verification = NOT EXECUTED: Docker daemon pipe was inactive on host; native PostgreSQL 17 service used on 127.0.0.1:5432`.

### Вариант Б: Локальный PostgreSQL 17
Если PostgreSQL установлен локально:
1. Создайте пользователя и базу данных:
   ```sql
   CREATE USER backup_app WITH PASSWORD 'backup_app_dev_secret_2026' CREATEDB;
   CREATE DATABASE backup_system OWNER backup_app;
   ```
2. Убедитесь, что служба PostgreSQL слушает порт `5432`.

---

## 6. Применение миграций базы данных (Migrations Policy)

> [!IMPORTANT]
> **Политика миграций:** В соответствии с промышленным стандартом сервис `Backup.MetadataService` **НЕ выполняет** автоматических неконтролируемых миграций при старте. Метод `Database.EnsureCreated()` запрещен. Миграции накатаны и задокументированы через официальный CLI Entity Framework Core.

Команда применения миграций из корня репозитория:
```powershell
dotnet ef database update --project src/Backup.MetadataService --startup-project src/Backup.MetadataService
```

*Применяемые сущности:*
- `clients` — зарегистрированные рабочие станции;
- `backup_sets` — наборы резервного копирования;
- `files` — метаданные файлов (`UNIQUE (BackupSetId, RelativePath)`);
- `file_versions` — зафиксированные ревизии (`UNIQUE (FileId, VersionNumber)`, `UNIQUE (OperationId)`);
- `change_logs` — журнал аудита событий (`CREATE`, `MODIFY`, `DELETE`, `RENAME`, `RESTORE`);
- `pending_file_operations` — подготовленные транзакции загрузки (`OperationKind`, `ExpiresAtUtc`).

---

## 7. Сборка и тестирование (Build & Test)

```powershell
# 1. Восстановление зависимостей
dotnet restore

# 2. Сборка всего решения
dotnet build

# 3. Запуск полного набора тестов (171 тест)
dotnet test
```

### Структура тестового набора (171 тест):
- **Unit-тесты (113 тестов):**
  - Protocol v1 фрейминг, валидаторы заголовков и фрагментированные TCP-потоки;
  - `ProtocolPathValidator` — защита от path traversal, абсолютных путей, UNC, спецсимволов;
  - Бизнес-логика `MetadataApplicationService` — идемпотентность `CommitVersion`, логическое удаление, переименования;
  - `FileStorageService` и `StorageKeyHelper` — детерминированные ключи, инкрементальный SHA-256;
  - `RestoreSuppressionService` — подавление эхо-событий наблюдателя;
  - Фоновые воркеры очистки `PendingOperationsCleanupWorker` и `StorageTempCleanupWorker`;
  - Диагностический верификатор целостности `StorageConsistencyVerifier`.
- **Интеграционные тесты (50 тестов):**
  - TCP-сокеты метаданных и физического хранилища;
  - Оркестрация `SyncGateway` со streaming relay;
  - Отказоустойчивость: жизненный цикл Client Reconnect с backoff;
  - Изоляция нескольких одновременных клиентов на одном шлюзе (`MultipleClientsTests`);
  - Потоковая передача больших файлов 32 MiB (`LargeFileStreamingTests`);
  - Восстановление после перезапуска и офлайн-модификаций (`RestartRecoveryTests`).
- **Комплексные сквозные E2E тесты (8 тестов):**
  - Полный жизненный цикл автоматической синхронизации директории;
  - Сквозной цикл восстановления версии с pre-backup защитой несохраненных правок;
  - Финальный детерминированный сценарий демонстрации `FinalDemoE2ETests` (фазы A—H, 29 шагов).

---

## 8. Ручной запуск сервисов (Troubleshooting / Manual Mode)

> Для повседневной разработки и демонстрации рекомендуется единая команда: **`.\start.ps1`**.
> Ручной запуск в отдельных терминалах приведен ниже для отладки и детального мониторинга:

Запуск осуществляется в **четырех отдельных терминалах PowerShell** из корня репозитория:

### Терминал 1: Сервис метаданных (MetadataService)
```powershell
dotnet run --project src/Backup.MetadataService
```
*Ожидаемый вывод:*
```text
info: Backup.MetadataService.MetadataServerWorker[0]
      [MetadataService] Listening on 127.0.0.1:5001
```

### Терминал 2: Сервис хранилища (StorageService)
```powershell
dotnet run --project src/Backup.StorageService
```
*Ожидаемый вывод:*
```text
info: Backup.StorageService.StorageServerWorker[0]
      [StorageService] Listening on 127.0.0.1:5002
```

### Терминал 3: Шлюз синхронизации (SyncGateway)
```powershell
dotnet run --project src/Backup.SyncGateway
```
*Ожидаемый вывод:*
```text
info: Backup.SyncGateway.GatewayServerWorker[0]
      [SyncGateway] MetadataService handshake OK
info: Backup.SyncGateway.GatewayServerWorker[0]
      [SyncGateway] StorageService handshake OK
info: Backup.SyncGateway.GatewayServerWorker[0]
      [SyncGateway] Listening on 127.0.0.1:5000
```

### Терминал 4: Клиентский веб-интерфейс (Client.Web)
```powershell
dotnet run --project src/Backup.Client.Web
```
*Ожидаемый вывод:*
```text
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5100
```

Веб-интерфейс доступен по адресу: **http://localhost:5100**

---

## 9. Пошаговый сценарий демонстрации (Demo Walkthrough)

1. Откройте в браузере `http://localhost:5100`.
2. Создайте тестовую папку на диске, например `C:\demo_sync` (или любую изолированную директорию).
3. На главной странице (`/`) введите имя набора (например, `DemoSet`) и абсолютный путь к папке. Нажмите **«Создать / Выбрать профиль»**.
4. Нажмите **«Старт синхронизации»**.
5. Создайте в тестовой папке файл `test.txt` с текстом `Version 1`.
6. В течение секунды файл синхронизируется. В интерфейсе на странице `/files` появится запись о файле с версией 1.
7. Отредактируйте файл `test.txt`, изменив текст на `Version 2`. В интерфейсе версия обновится до 2.
8. Перейдите на страницу ревизий `/files/{id}`: отображаются версии 1 и 2.
9. Нажмите кнопку **«Восстановить»** напротив Версии 1 и подтвердите действие.
10. Содержимое файла `test.txt` на диске мгновенно возвращается к состоянию `Version 1`. В системе создается новая неизменяемая версия 3 (`RESTORE`), защищающая целостность цепочки версий.
11. Перейдите на страницу `/history` и проверьте записи журнала аудита: `CREATE`, `MODIFY`, `RESTORE`.

---

## 10. Диагностика и устранение неполадок (Troubleshooting)

- **Ошибка `METADATA_UNAVAILABLE`:**
  - Проверьте, запущен ли Терминал 1 (`Backup.MetadataService`) и свободен ли порт `5001`.
  - Проверьте доступность PostgreSQL на порту `5432`.
- **Ошибка `STORAGE_UNAVAILABLE`:**
  - Проверьте, запущен ли Терминал 2 (`Backup.StorageService`) и доступен ли каталог `data/storage`.
- **Клиент отображает статус шлюза `Offline`:**
  - Проверьте, запущен ли Терминал 3 (`Backup.SyncGateway`). При восстановлении работы шлюза клиент автоматически выполнит reconnect и запустит реконсиляцию.
- **Ошибки блокировки файлов (`File locked by another process`):**
  - Приложение использует `FileStabilityHelper` с автоматическими повторными попытками. Если файл долго занят внешним редактором, операция откладывается без падения клиента, выставляя флаг `ReconciliationRequired`.

---

## 11. Расположение данных (Data Locations)

- `data/client/client-state.json` — локальный идентификатор зарегистрированной рабочей станции (`ClientId`).
- `data/client/sync-profile.json` — настройки активного набора синхронизации и локального пути.
- `data/client/index/{BackupSetId}.json` — локальный кеш контрольных сумм файлов для быстрого сканирования.
- `data/storage/.tmp/{OpId}.part` — временные файлы незавершенных загрузок (автоматически очищаются).
- `data/storage/clients/{clientId}/.../versions/{version}.bin` — постоянные неизменяемые блобы версий файлов.
- База данных PostgreSQL (`backup_system`) — таблицы метаданных, версий, наборов и журнала аудита.

---

## 12. Что намеренно не входит в рамки v1 (Scope Exclusions)

В соответствии с техническим заданием и учебным форматом курсового проекта следующие компоненты намеренно оставлены за пределами текущей реализации:
- Автоматическое удаление зафиксированных физических блобов («Orphan Committed Blobs») при отсутствии ссылок в метаданных — во избежание безвозвратной потери данных при сетевых тайм-аутах;
- Мультиплексирование параллельных логических потоков внутри одного TCP-соединения (используется модель одно соединение — одна последовательная сессия);
- Брокеры сообщений (RabbitMQ, Kafka) и внешние распределенные кэши (Redis);
- Распределенный двухфазный коммит (2PC) — заменен надежным Staged Workflow (`Prepare` -> `Store` -> `Commit`);
- Протокольное шифрование TLS и авторизация пользователей;
- Дельта-компрессия и поблочная дедупликация на уровне чанков.
