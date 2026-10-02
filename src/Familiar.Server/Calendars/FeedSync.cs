using System.Net;
using System.Net.Sockets;
using Familiar.Domain.Calendars;

namespace Familiar.Server.Calendars;

public sealed class CalendarOptions
{
    public const string Section = "Calendars";

    /// <summary>How often a subscribed feed is fetched again.</summary>
    public int RefreshMinutes { get; init; } = 30;

    /// <summary>How far ahead feeds are expanded.</summary>
    public int DaysAhead { get; init; } = 60;

    /// <summary>A feed bigger than this is refused (years of history from a busy shared calendar).</summary>
    public int MaxFeedBytes { get; init; } = 10 * 1024 * 1024;
}

/// <summary>
/// Fetches subscribed iCal feeds (PLAN.md §6.4) and keeps their events current: on a schedule, and at
/// once when one is added.
/// </summary>
public sealed class FeedSync(
    CalendarStore store,
    IHttpClientFactory httpFactory,
    CalendarOptions options,
    TimeProvider clock,
    ILogger<FeedSync> logger) : BackgroundService
{
    /// <summary>
    /// Whether a URL is one Familiar will fetch: http(s) or webcal, and not this machine or a
    /// link-local address. The local network is allowed - a Nextcloud on the LAN is a fine
    /// calendar - so this is a guard against mistakes and the obvious abuses, not a sandbox.
    /// </summary>
    public static Uri? Acceptable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048 || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        // webcal:// is just "subscribe to this with your calendar app" - it's https underneath.
        if (uri.Scheme.Equals("webcal", StringComparison.OrdinalIgnoreCase))
        {
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return null;
        }

        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)
            && (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
                || (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [169, 254, ..])
                || address.IsIPv6LinkLocal))
        {
            return null;
        }

        return uri;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = await store.FeedsDueAsync(clock.GetUtcNow().AddMinutes(-Math.Max(1, options.RefreshMinutes)), stoppingToken);
                foreach (var source in due)
                {
                    await SyncAsync(source, stoppingToken);
                }

                await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.FeedPassFailed(exception);
                await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>Fetches one feed now. Null when it worked; otherwise why not, as the user should see it.</summary>
    public async Task<string?> SyncAsync(CalendarSource source, CancellationToken ct)
    {
        string? error;
        try
        {
            var text = await FetchAsync(store.FeedUrl(source), ct);
            var now = clock.GetUtcNow();
            var occurrences = IcsFeed.Read(text, now.AddDays(-1), now.AddDays(Math.Max(1, options.DaysAhead)));
            await store.ReplaceFeedEventsAsync(source, occurrences, ct);
            logger.FeedSynced(source.Id, occurrences.Count);
            return null;
        }
        catch (HttpRequestException exception)
        {
            error = exception.StatusCode is { } status
                ? $"The calendar server answered {(int)status} {status}. Check the address is still valid."
                : "Couldn't reach the calendar server.";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            error = "The calendar server didn't answer in time.";
        }
        catch (FormatException exception)
        {
            error = exception.Message;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            error = "The saved address can't be read any more (the server's keys changed). Remove this calendar and add it again.";
        }

        logger.FeedFailed(source.Id, error);
        await store.MarkFailedAsync(source.Id, error, ct);
        return error;
    }

    private async Task<string> FetchAsync(Uri url, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(nameof(FeedSync));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > options.MaxFeedBytes)
        {
            throw new FormatException("That calendar is too big to subscribe to.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (limited.Length + read > options.MaxFeedBytes)
            {
                throw new FormatException("That calendar is too big to subscribe to.");
            }

            limited.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(limited.GetBuffer(), 0, (int)limited.Length);
    }
}
