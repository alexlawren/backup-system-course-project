using Backup.StorageService;
using Backup.StorageService.Configuration;
using Backup.StorageService.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<StorageServiceOptions>(
    builder.Configuration.GetSection(StorageServiceOptions.SectionName));

builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection(StorageOptions.SectionName));

builder.Services.Configure<StorageTempCleanupOptions>(
    builder.Configuration.GetSection(StorageTempCleanupOptions.SectionName));

builder.Services.AddSingleton<IFileStorageService, FileStorageService>();
builder.Services.AddHostedService<StorageServerWorker>();
builder.Services.AddHostedService<StorageTempCleanupWorker>();

var host = builder.Build();
await host.RunAsync();
