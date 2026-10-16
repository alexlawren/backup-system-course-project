using System.Text.Json;

namespace Backup.Client.Web.Services;

public class ClientState
{
    public Guid? ClientId { get; set; }
    public string MachineName { get; set; } = Environment.MachineName;
    public string ClientName { get; set; } = Environment.MachineName;
    public DateTime? RegisteredAtUtc { get; set; }
}

public interface IClientIdentityService
{
    Task<ClientState> GetStateAsync(CancellationToken cancellationToken = default);
    Task SaveStateAsync(Guid clientId, string? machineName = null, string? clientName = null, CancellationToken cancellationToken = default);
}

public class ClientIdentityService : IClientIdentityService
{
    private readonly string _stateFilePath;
    private readonly string? _defaultMachineName;
    private readonly string? _defaultClientName;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private ClientState? _cachedState;

    public ClientIdentityService(string? customStateFilePath = null, string? defaultMachineName = null, string? defaultClientName = null)
    {
        _stateFilePath = customStateFilePath ?? ResolveDefaultStateFilePath();
        _defaultMachineName = defaultMachineName;
        _defaultClientName = defaultClientName;
    }

    public async Task<ClientState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (_cachedState != null)
            {
                return _cachedState;
            }

            if (!File.Exists(_stateFilePath))
            {
                _cachedState = new ClientState
                {
                    ClientId = null,
                    MachineName = _defaultMachineName ?? Environment.MachineName,
                    ClientName = _defaultClientName ?? _defaultMachineName ?? Environment.MachineName
                };
                return _cachedState;
            }

            string json = await File.ReadAllTextAsync(_stateFilePath, cancellationToken);
            _cachedState = JsonSerializer.Deserialize<ClientState>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new ClientState();

            if (!string.IsNullOrWhiteSpace(_defaultMachineName))
            {
                _cachedState.MachineName = _defaultMachineName;
            }
            if (!string.IsNullOrWhiteSpace(_defaultClientName))
            {
                _cachedState.ClientName = _defaultClientName;
            }

            return _cachedState;
        }
        catch
        {
            _cachedState = new ClientState
            {
                ClientId = null,
                MachineName = Environment.MachineName,
                ClientName = Environment.MachineName
            };
            return _cachedState;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task SaveStateAsync(
        Guid clientId,
        string? machineName = null,
        string? clientName = null,
        CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var state = _cachedState ?? new ClientState();
            state.ClientId = clientId;
            state.MachineName = machineName ?? state.MachineName ?? Environment.MachineName;
            state.ClientName = clientName ?? state.ClientName ?? state.MachineName;
            state.RegisteredAtUtc = DateTime.UtcNow;

            string? dir = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempFile = _stateFilePath + ".tmp";
            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(tempFile, json, cancellationToken);
            File.Move(tempFile, _stateFilePath, overwrite: true);

            _cachedState = state;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static string ResolveDefaultStateFilePath()
    {
        string configuredPath = "data/client/client-state.json";

        // Find solution root
        string? current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "BackupSystem.sln")))
            {
                return Path.GetFullPath(Path.Combine(current, configuredPath));
            }
            current = Directory.GetParent(current)?.FullName;
        }

        string? baseDir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(baseDir))
        {
            if (File.Exists(Path.Combine(baseDir, "BackupSystem.sln")))
            {
                return Path.GetFullPath(Path.Combine(baseDir, configuredPath));
            }
            baseDir = Directory.GetParent(baseDir)?.FullName;
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configuredPath));
    }
}
