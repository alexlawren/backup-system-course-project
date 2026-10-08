# Клиент-серверная система резервного копирования файлов

Курсовой проект по дисциплине «Программирование сетевых приложений».

**Change-set #6:** RESTORE FLOW + FILE/VERSION BROWSER (полноценное восстановление выбранной исторической версии файла из StorageService обратно в отслеживаемую локальную папку клиента; неизменяемость версий с инкрементом `CurrentVersionNumber`; пред-бэкап несохраненных локальных правок пользователя; потоковая ретрансляция со шлюза с ограниченным буфером 64 KiB без промежуточных файлов на диске Gateway; временный файл в каталоге назначения с инкрементальной проверкой SHA-256 и размера; атомарная замена `File.Move`; интеллектуальное подавление эхо-событий FileSystemWatcher через `RestoreSuppressionService`; сохранение происхождения операции `OperationKind` в `pending_file_operations` и фиксация `Action = RESTORE` в `change_logs`; восстановление удаленных файлов с реактивацией; защита от гонок потоков через сериализацию сокета и per-path lock; веб-страницы `/files`, `/files/{fileId}`, `/history` на Blazor Server через Gateway).

---

## 1. Системные требования

- **Операционная система:** Windows / Linux / macOS.
- **Платформа:** .NET 9 SDK (целевая платформа `net9.0`).
- **Entity Framework Core CLI:** `dotnet ef` (версия 9.0.x).
- **База данных:** PostgreSQL 17 (локально или через Docker Compose).

---

## 2. Развертывание PostgreSQL

### Вариант А: Использование Docker Compose (рекомендуется)

1. Убедитесь, что Docker Desktop запущен.
2. Скопируйте `.env.example` в `.env` (при необходимости скорректируйте пароль):
   ```powershell
   Copy-Item .env.example .env
   ```
3. Запустите контейнер PostgreSQL:
   ```powershell
   docker compose up -d
   ```
4. Проверьте статус контейнера:
   ```powershell
   docker compose ps
   ```

### Вариант Б: Локальный экземпляр PostgreSQL 17

Если PostgreSQL установлен локально в системе:
1. Создайте пользователя и базу данных (пароль по умолчанию `backup_app_dev_secret_2026`):
   ```sql
   CREATE USER backup_app WITH PASSWORD 'backup_app_dev_secret_2026' CREATEDB;
   CREATE DATABASE backup_system OWNER backup_app;
   ```
2. Строка подключения конфигурируется в `src/Backup.MetadataService/appsettings.json` или через переменную окружения `ConnectionStrings__MetadataDb`.

---

## 3. Применение миграций базы данных

После запуска PostgreSQL выполните обновление схемы через EF Core CLI:

```powershell
dotnet ef database update --project src/Backup.MetadataService --startup-project src/Backup.MetadataService
```

*Создаются и обновляются таблицы:*
- `clients` — зарегистрированные клиенты;
- `backup_sets` — наборы резервного копирования;
- `files` — метаданные файлов с `UNIQUE (BackupSetId, RelativePath)`;
- `file_versions` — зафиксированные ревизии с `UNIQUE (FileId, VersionNumber)` и `UNIQUE (OperationId)` для идемпотентного коммита;
- `change_logs` — аудит и журнал событий (`CREATE`, `MODIFY`, `DELETE`, `RENAME`, `RESTORE`);
- `pending_file_operations` — подготовленные транзакции загрузки с полем `OperationKind` (`"NORMAL"`, `"RESTORE"`).

---

## 4. Сборка и тестирование решения

```powershell
# Восстановление зависимостей
dotnet restore

# Сборка решения
dotnet build

# Запуск полного набора модульных и интеграционных тестов (157 тестов)
dotnet test
```

Тестовый набор включает:
- 12 тестов фрейминга и потокового чтения Protocol v1 (включая фрагментированные TCP-потоки);
- 19 тестов валидации относительных путей `ProtocolPathValidator` (защита от path traversal, абсолютных путей, спецсимволов);
- 22 теста бизнес-логики `MetadataApplicationService` (включая 6 тестов идемпотентности `CommitVersion`, защиту от повторного создания версии и валидацию конфликтов);
- 2 комплексных интеграционных теста метаданных (персистентность в PostgreSQL и сокеты TCP Protocol v1);
- 12 модульных тестов `FileStorageService` и `StorageKeyHelper` (детерминированный ключ, валидация SHA-256, файлы нулевого размера, защита от path traversal, очистка temp-файлов);
- 26 интеграционных TCP-тестов физического хранилища (сквозной цикл `StoreFile` -> `StoreFileReady` -> `UploadFile` -> `StoreFileResult`, скачивание `GetStoredFile`, конкурентные клиенты, большие файлы 8 MiB, обнаружение конфликтов и идемпотентность, проверка обрыва соединения);
- 1 сквозной smoke-тест взаимодействия `Backup.SyncGateway` и `Backup.StorageService`;
- 13 тестов оркестрации `SyncGateway` (решение `UploadRequired`, изоляция сессий, no interleaving, streaming relay без буферизации файла, валидация путей, обработка сбоев хранилища, retry при тайм-ауте `CommitVersion`);
- 2 комплексных E2E-интеграционных теста жизненного цикла резервного копирования одиночного файла;
- 1 живой интеграционный smoke-тест верификации 4 сервисов;
- 9 тестов согласования манифестов `Manifest` $\rightarrow$ `SyncPlan`, снимков сервера без N+1 запросов и исправления семантики возврата удаленного файла;
- 6 тестов локального кеша индексов `LocalFileIndexService` и сканера (повторное использование хэша, пересчет, битый кеш, удаление файлов, атомарная запись);
- 9 тестов `FileSystemWatcher` и дебаунса (схлопывание `Changed`, `Created` + `Changed`, удаление, переименования, независимость файлов, переполнение буфера, обработка пред-сканированных событий);
- 2 комплексных E2E-теста полного жизненного цикла автоматической синхронизации директории (Create $\rightarrow$ Modify $\rightarrow$ Rename $\rightarrow$ Delete $\rightarrow$ Recreate) и начальной офлайн-реконсиляции на реальном кластере PostgreSQL + Storage + Gateway;
- 8 тестов потокового скачивания и безопасности восстановления `RestoreDownloadTests` (разрешение версий, ошибка отсутствующей версии, binary round-trip, большой файл 5 МБ, откат при расхождении размера/хэша, отказ при path traversal, воссоздание удаленных родительских каталогов);
- 6 тестов интеллектуального подавления эхо-событий `RestoreWatcherSuppressionTests` (подавление по хэшу, множественные всплески, реакция на новые правки с другим хэшем, истечение TTL, фильтрация по относительному пути);
- 3 интеграционных теста семантики версионирования `RestoreVersionSemanticsTests` (v1 $\rightarrow$ v2 $\rightarrow$ v3 $\rightarrow$ restore v1 создает v4 с хэшем v1, неизменяемость истории, ChangeLog RESTORE, реактивация удаленного файла, идемпотентный пропуск дубликата для текущей версии);
- 4 комплексных E2E-сценария `RestoreE2ETests` (полный сквозной цикл восстановления на реальных сокетах с проверкой отсутствия v5 от наблюдателя, защита несохраненных локальных правок через pre-backup, удаление с последующим восстановлением, и браузерные запросы метаданных через Gateway).

---

## 5. Сетевые порты сервисов

| Сервис | Протокол | Хост / Адрес | Назначение |
| :--- | :--- | :--- | :--- |
| **PostgreSQL** | TCP | `127.0.0.1:5432` | Хранилище метаданных (`backup_system`) |
| **Backup.MetadataService** | TCP (Protocol v1) | `127.0.0.1:5001` | Микросервис метаданных и версионирования |
| **Backup.StorageService** | TCP (Protocol v1) | `127.0.0.1:5002` | Сервис физического хранения файлов |
| **Backup.SyncGateway** | TCP (Protocol v1) | `127.0.0.1:5000` | Внешний шлюз синхронизации и оркестратор |
| **Backup.Client.Web** | HTTP (Blazor Server) | `http://localhost:5100` | Локальный веб-интерфейс клиента |

---

## 6. Запуск сервисов

Сервисы запускаются в отдельных терминалах в следующем порядке:

### Терминал 1: Сервис метаданных (MetadataService)
```powershell
dotnet run --project src/Backup.MetadataService
```
*Ожидаемый лог:*
```text
info: Backup.MetadataService.MetadataServerWorker[0]
      [MetadataService] Listening on 127.0.0.1:5001
```

### Терминал 2: Сервис хранилища (StorageService)
```powershell
dotnet run --project src/Backup.StorageService
```
*Ожидаемый лог:*
```text
info: Backup.StorageService.StorageServerWorker[0]
      [StorageService] Listening on 127.0.0.1:5002
```

### Терминал 3: Шлюз синхронизации (SyncGateway)
```powershell
dotnet run --project src/Backup.SyncGateway
```
*Ожидаемый лог:*
```text
info: Backup.SyncGateway.GatewayServerWorker[0]
      [SyncGateway] MetadataService handshake OK
info: Backup.SyncGateway.GatewayServerWorker[0]
      [SyncGateway] StorageService handshake OK
info: Backup.SyncGateway.GatewayServerWorker[0]
      [SyncGateway] Listening on 127.0.0.1:5000
```

### Терминал 4: Локальный веб-клиент (Client.Web)
```powershell
dotnet run --project src/Backup.Client.Web
```
Веб-интерфейс доступен по адресу: **http://localhost:5100**

---

## 7. Остановка Docker-контейнера

После завершения работы для остановки контейнера без удаления данных выполните:
```powershell
docker compose stop
```
*(Не используйте флаг `-v`, чтобы не удалить том `backup_system_postgres_data` с данными).*
