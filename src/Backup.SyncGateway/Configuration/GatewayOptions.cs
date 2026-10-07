namespace Backup.SyncGateway.Configuration;

public class GatewayOptions
{
    public const string SectionName = "GatewayOptions";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5000;
}

public class DownstreamServiceEndpoint
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
}

public class DownstreamServicesOptions
{
    public const string SectionName = "DownstreamServices";

    public DownstreamServiceEndpoint MetadataService { get; set; } = new() { Host = "127.0.0.1", Port = 5001 };
    public DownstreamServiceEndpoint StorageService { get; set; } = new() { Host = "127.0.0.1", Port = 5002 };
}
