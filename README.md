# Клиент-серверная система резервного копирования файлов

Курсовой проект по дисциплине «Программирование сетевых приложений».

**Change-set #4:** END-TO-END BACKUP ORCHESTRATION (сквозная оркестрация резервного копирования одиночного файла: `Client.Web` -> `SyncGateway` -> `MetadataService` + `StorageService`. Рабочий процесс `PrepareVersion` -> `UploadRequired` -> binary streaming `UploadFile` через Gateway -> `StoreFile` -> идемпотентный `CommitVersion` с retry -> `UploadCompleted`. Потоковый zero-copy relay на шлюзе, изоляция клиентских сессий, постоянная идентификация клиента в `data/client/client-state.json`, проксирование регистрации и создания наборов через Gateway, минимальный диагностический UI для ручной верификации).

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
- `change_logs` — аудит и журнал событий (`CREATE`, `MODIFY`, `DELETE`, `RENAME`);
- `pending_file_operations` — подготовленные транзакции загрузки (Prepare $\rightarrow$ Commit).

---

## 4. Сборка и тестирование решения

```powershell
# Восстановление зависимостей
dotnet restore

# Сборка решения
dotnet build

# Запуск полного набора модульных и интеграционных тестов (110 тестов)
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
- 13 тестов оркестрации `SyncGateway` (решение `UploadRequired`, изоляция сессий, no interleaving, zero-copy relay, валидация путей, обработка сбоев хранилища, retry при тайм-ауте `CommitVersion`);
- 2 комплексных E2E-интеграционных теста жизненного цикла резервного копирования v1 $\rightarrow$ v2 и сценария сбоя на реальном кластере;
- 1 живой интеграционный smoke-тест верификации 4 сервисов.

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
