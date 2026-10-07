using Backup.SyncGateway;
using Backup.SyncGateway.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<GatewayOptions>(
    builder.Configuration.GetSection(GatewayOptions.SectionName));

builder.Services.Configure<DownstreamServicesOptions>(
    builder.Configuration.GetSection(DownstreamServicesOptions.SectionName));

builder.Services.AddHostedService<GatewayServerWorker>();

var host = builder.Build();
await host.RunAsync();
