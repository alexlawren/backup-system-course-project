using Backup.StorageService;
using Backup.StorageService.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<StorageServiceOptions>(
    builder.Configuration.GetSection(StorageServiceOptions.SectionName));

builder.Services.AddHostedService<StorageServerWorker>();

var host = builder.Build();
await host.RunAsync();
