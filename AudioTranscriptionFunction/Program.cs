using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

// Register core worker defaults so non-HTTP triggers (Queue/Blob) function correctly.
builder.Services.AddFunctionsWorkerDefaults();
// Optional HTTP pipeline integration (safe even if no HTTP triggers yet)
builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Add startup container verification service
builder.Services.AddHostedService<AudioTranscriptionFunction.StorageContainerInitializer>();

builder.Build().Run();
