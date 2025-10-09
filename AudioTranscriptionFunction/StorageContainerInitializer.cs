using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AudioTranscriptionFunction
{
    /// <summary>
    /// Ensures required blob containers exist using the AzureWebJobsStorage connection string.
    /// </summary>
    internal class StorageContainerInitializer : IHostedService
    {
        private static readonly string[] RequiredContainers = new[] { "audio-input", "transcript-output" };
        private readonly ILogger<StorageContainerInitializer> _logger;

        public StorageContainerInitializer(ILogger<StorageContainerInitializer> logger)
        {
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var conn = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            if (string.IsNullOrWhiteSpace(conn))
            {
                _logger.LogError("[Startup] AzureWebJobsStorage connection string not set.");
                return;
            }
            if (conn.Contains("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[Startup] Using development storage.");
            }

            BlobServiceClient serviceClient;
            try
            {
                serviceClient = new BlobServiceClient(conn);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Startup] Failed creating BlobServiceClient from connection string.");
                return;
            }

            foreach (var name in RequiredContainers)
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var container = serviceClient.GetBlobContainerClient(name);
                    await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
                    sw.Stop();
                    _logger.LogInformation("[Startup] Ensured container '{Container}' exists (elapsed {Ms}ms)", name, sw.ElapsedMilliseconds);
                }
                catch (RequestFailedException rfe)
                {
                    _logger.LogError(rfe, "[Startup] RequestFailed ensuring container '{Container}'", name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Startup] Unexpected error ensuring container '{Container}'", name);
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
