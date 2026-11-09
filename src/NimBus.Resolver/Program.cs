using System.Globalization;
using NimBus.Resolver;
using NimBus.Core.Extensions;
using NimBus.MessageStore;
using NimBus.MessageStore.SqlServer;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.AddServiceDefaults(options => options.UseStandardResilienceHandler = true);

// Configure OpenTelemetry for distributed tracing and metrics
builder.Services
    .AddOpenTelemetry()
    .UseFunctionsWorkerDefaults();

// Configure Serilog for structured logging (console output)
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(LogEventLevel.Information, formatProvider: CultureInfo.InvariantCulture)
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Services.AddLogging(loggingBuilder => loggingBuilder.AddSerilog(dispose: true));

var storageProvider = ResolverStorageProvider.Select(builder.Configuration);

builder.Services.AddNimBus(nimbus =>
{
    if (ResolverStorageProvider.IsSqlServer(storageProvider))
    {
        nimbus.AddSqlServerMessageStore();
    }
    else
    {
        nimbus.AddCosmosDbMessageStore();
    }
});

builder.Services.AddResolver();

builder.Build().Run();
