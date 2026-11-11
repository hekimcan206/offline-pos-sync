using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace PosSync.Server.Infrastructure;

/// <summary>
/// Database contention and outages become a retryable 503 with Retry-After. A device treats any 5xx like a dropped
/// link: its changes stay in the outbox and the next round retries them, which is safe because pushes are idempotent.
/// </summary>
internal sealed class DatabaseExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var retryable = exception switch
        {
            PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } => true,
            NpgsqlException { IsTransient: true } => true,
            TimeoutException => true,
            _ => false,
        };

        if (!retryable)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = "1";
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Temporarily unavailable, retry.",
            },
            Exception = exception,
        });
    }
}
