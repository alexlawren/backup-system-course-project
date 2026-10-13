using Backup.Client.Web.Components;
using Backup.Client.Web.Configuration;
using Backup.Client.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

// Configure Gateway options
builder.Services.Configure<GatewayOptions>(
    builder.Configuration.GetSection(GatewayOptions.SectionName));

// Register Client services
builder.Services.AddSingleton<IClientIdentityService, ClientIdentityService>();
builder.Services.AddSingleton<BackupGatewayClient>();
builder.Services.AddScoped<IBackupClientService, BackupClientService>();

builder.Services.AddSingleton<ISyncProfileService, SyncProfileService>();
builder.Services.AddSingleton<ILocalFileIndexService, LocalFileIndexService>();
builder.Services.AddSingleton<IFileStabilityHelper, FileStabilityHelper>();
builder.Services.AddSingleton<IDirectoryScanner, DirectoryScanner>();
builder.Services.AddSingleton<IFileSystemWatcherService, FileSystemWatcherService>();
builder.Services.AddSingleton<IRestoreSuppressionService, RestoreSuppressionService>();
builder.Services.AddSingleton<ISyncEngine, SyncEngine>();
builder.Services.AddHostedService<ClientStartupSyncWorker>();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program { }
