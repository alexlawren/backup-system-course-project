# Клиент-серверная система резервного копирования файлов

Курсовой проект по дисциплине «Программирование сетевых приложений».

**Change-set #1:** Архитектурный каркас решения, ядро собственного сетевого TCP Protocol v1 и smoke-тест сетевого взаимодействия.

---

## 1. Системные требования

- **Операционная система:** Windows / Linux / macOS.
- **Платформа:** .NET 9 SDK (целевая платформа `net9.0`).
- Дополнительные СУБД, Docker и внешние брокеры сообщений на данном этапе **не требуются**.

---

## 2. Сборка и тестирование

Выполните следующие команды из корневой папки репозитория:

```bash
# Восстановление зависимостей
dotnet restore

# Сборка всего решения
dotnet build

# Запуск модульных тестов сетевого протокола
dotnet test
```

Все 12 модульных тестов сетевого протокола (включая проверку фрагментированного TCP-потока, ограничения длины заголовка 64 KiB, потоковую передачу бинарных данных без повреждений и валидацию полей) должны завершиться успешно.

---

## 3. Сетевые порты и адреса сервисов

| Сервис | Протокол | Хост / Адрес | Назначение |
| :--- | :--- | :--- | :--- |
| **Backup.MetadataService** | TCP (Protocol v1) | `127.0.0.1:5001` | Сервис метаданных и ревизий |
| **Backup.StorageService** | TCP (Protocol v1) | `127.0.0.1:5002` | Сервис физического хранения файлов |
| **Backup.SyncGateway** | TCP (Protocol v1) | `127.0.0.1:5000` | Внешний шлюз синхронизации и оркестратор |
| **Backup.Client.Web** | HTTP (Blazor Server) | `http://localhost:5100` | Локальный веб-интерфейс клиента |

---

## 4. Порядок и команды запуска приложений

Для демонстрации корректного межсетевого взаимодействия сервисы рекомендуется запускать в отдельных терминалах в следующем порядке:

### Терминал 1: Запуск сервиса метаданных
```bash
dotnet run --project src/Backup.MetadataService
```
*Ожидаемый лог:*
```text
info: Backup.MetadataService.MetadataServerWorker[0]
      [MetadataService] Listening on 127.0.0.1:5001
```

### Терминал 2: Запуск сервиса хранения
```bash
dotnet run --project src/Backup.StorageService
```
*Ожидаемый лог:*
```text
info: Backup.StorageService.StorageServerWorker[0]
      [StorageService] Listening on 127.0.0.1:5002
```

### Терминал 3: Запуск шлюза синхронизации
```bash
dotnet run --project src/Backup.SyncGateway
```
*Ожидаемый лог при старте (Gateway выполняет startup smoke check к обоим downstream-сервисам):*
```text
info: Backup.SyncGateway.GatewayServerWorker[0]
      [SyncGateway] MetadataService handshake OK
info: Backup.SyncGateway.GatewayServerWorker[0]
      [SyncGateway] StorageService handshake OK
info: Backup.SyncGateway.GatewayServerWorker[0]
      [SyncGateway] Listening on 127.0.0.1:5000
```
> *Примечание:* Если MetadataService или StorageService запущены позже, SyncGateway не завершается аварийно, а выводит понятное предупреждение (`[SyncGateway] Downstream MetadataService at 127.0.0.1:5001 is unavailable at startup: ...`).

### Терминал 4: Запуск локального веб-клиента
```bash
dotnet run --project src/Backup.Client.Web
```
*Ожидаемый лог:*
```text
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5100
```
После запуска откройте в браузере: **http://localhost:5100**

---

## 5. Документация проекта

- [Архитектура решения](docs/architecture.md) — обзор взаимодействия компонентов и распределение обязанностей.
- [Спецификация Protocol v1](docs/protocol-v1.md) — физическая структура кадра, формат заголовка, список типов сообщений и правила потокового чтения/записи.
