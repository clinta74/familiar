using System.Globalization;
using System.Threading.RateLimiting;
using Familiar.Server.Auth;
using Microsoft.Net.Http.Headers;

namespace Familiar.Server.Infrastructure;

/// <summary>
/// Request rate limits. Partitioned by client address, which is only meaningful once forwarded
/// headers are configured (<see cref="ProxyOptions"/>): behind a proxy without them, every request
/// shares the proxy's address and one partition, and anyone can exhaust it for everyone.
/// </summary>
public static class RateLimiting
{
    /// <summary>Registration, login, device sign-in and password changes - the guessable surfaces.</summary>
    public const string Auth = "auth";

    /// <summary>Messages to the familiar. Each one costs a model turn, so it's metered per account.</summary>
    public const string Conversation = "conversation";

    public sealed class Options
    {
        public const string Section = "RateLimits";

        public int AuthAttemptsPerMinute { get; set; } = 10;

        /// <summary>Generous for a person typing or talking; a script would hit it.</summary>
        public int MessagesPerMinute { get; set; } = 30;
    }

    public static IServiceCollection AddFamiliarRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(Options.Section).Get<Options>() ?? new Options();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.OnRejected = async (context, ct) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers[HeaderNames.RetryAfter] =
                        Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new ErrorResponse("Too many requests. Slow down and try again shortly."), ct);
            };

            limiter.AddPolicy(Auth, http => RateLimitPartition.GetFixedWindowLimiter(
                $"address:{http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, options.AuthAttemptsPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));

            // Per account (authentication runs before the limiter), so a household behind one
            // address doesn't share a budget.
            limiter.AddPolicy(Conversation, http => RateLimitPartition.GetTokenBucketLimiter(
                http.User.AccountId() is { } account ? $"account:{account}" : $"address:{http.Connection.RemoteIpAddress}",
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = Math.Max(1, options.MessagesPerMinute),
                    TokensPerPeriod = Math.Max(1, options.MessagesPerMinute),
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        return services;
    }
}
