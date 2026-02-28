using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using FlorisDeV.BackupClient.Config;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Provides Polly-based resilience pipelines for backup operations.
/// Uses Microsoft.Extensions.Http.Resilience which includes Polly v8 for standardized retry logic.
/// </summary>
public class ResiliencePipelineProvider
{
    public ResiliencePipelineProvider(IOptions<BackupClientOptions> options, ILogger<ResiliencePipelineProvider> logger)
    {
        var config = options.Value;

        // Build resilience pipeline for blob uploads
        BlobUploadPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = config.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(config.RetryDelayMs),
                BackoffType = DelayBackoffType.Exponential,
                MaxDelay = TimeSpan.FromMilliseconds(config.MaxRetryDelayMs),
                UseJitter = true, // Add jitter to avoid thundering herd
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransientError),
                OnRetry = args =>
                {
                    logger.LogWarning(
                        args.Outcome.Exception,
                        "Transient error in blob upload (attempt {Attempt}/{MaxAttempts}). " +
                        "Retrying after {DelayMs}ms. Error: {ErrorType}",
                        args.AttemptNumber + 1,
                        config.MaxRetryAttempts + 1,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.GetType().Name);

                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    /// Gets the resilience pipeline for blob upload operations.
    /// </summary>
    public ResiliencePipeline BlobUploadPipeline { get; }

    /// <summary>
    /// Determines if an exception represents a transient error that should be retried.
    /// </summary>
    private static bool IsTransientError(Exception ex)
    {
        return ex switch
        {
            // Azure SDK transient errors
            RequestFailedException rfe when IsTransientStatusCode(rfe.Status) => true,

            // Network-related errors
            HttpRequestException => true,
            TaskCanceledException { CancellationToken.IsCancellationRequested: false } => true, // Timeout
            TimeoutException => true,
            IOException => true,

            // Aggregate exceptions - check inner exceptions
            AggregateException ae => ae.InnerExceptions.Any(IsTransientError),

            _ => false
        };
    }

    /// <summary>
    /// Determines if an HTTP status code represents a transient error.
    /// </summary>
    private static bool IsTransientStatusCode(int statusCode)
    {
        return statusCode is 408 // Request Timeout
            or 429 // Too Many Requests
            or 500 // Internal Server Error
            or 502 // Bad Gateway
            or 503 // Service Unavailable
            or 504; // Gateway Timeout
    }
}
