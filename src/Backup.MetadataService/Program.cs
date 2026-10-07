using Backup.MetadataService;
using Backup.MetadataService.Configuration;
using Backup.MetadataService.Data;
using Backup.MetadataService.Services;
using Backup.Shared.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<MetadataServiceOptions>(
    builder.Configuration.GetSection(MetadataServiceOptions.SectionName));

var connectionString = builder.Configuration.GetConnectionString("MetadataDb")
    ?? "Host=127.0.0.1;Port=5432;Database=backup_system;Username=backup_app;Password=backup_app_dev_secret_2026";

builder.Services.AddDbContextFactory<MetadataDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

builder.Services.AddSingleton<ProtocolFrameReader>();
builder.Services.AddSingleton<ProtocolFrameWriter>();
builder.Services.AddSingleton<IMetadataApplicationService, MetadataApplicationService>();
builder.Services.AddSingleton<MetadataMessageDispatcher>();

builder.Services.AddHostedService<MetadataServerWorker>();

var host = builder.Build();
await host.RunAsync();
