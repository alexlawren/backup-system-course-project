namespace Backup.Client.Web.Configuration;

public class GatewayOptions
{
    public const string SectionName = "GatewayOptions";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5000;
}
