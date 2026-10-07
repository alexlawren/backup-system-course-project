using Backup.MetadataService;
using Backup.MetadataService.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<MetadataServiceOptions>(
    builder.Configuration.GetSection(MetadataServiceOptions.SectionName));

builder.Services.AddHostedService<MetadataServerWorker>();

var host = builder.Build();
await host.RunAsync();
