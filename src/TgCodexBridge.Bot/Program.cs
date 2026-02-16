using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TgCodexBridge.Bot;
using TgCodexBridge.Infrastructure.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
