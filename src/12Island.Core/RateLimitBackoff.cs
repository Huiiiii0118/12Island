using System.Net;

namespace TwelveIsland.Core;

// A 429. RetryAfter is what the server's Retry-After header said, as sent: a number of seconds or a date, either may be absent.
// RetryAt is when requests may go out again, set once a backoff has decided it. Sent is false for the answer TwelveIsland gave itself
// while a backoff was running, without asking the server.
public sealed class RateLimitedException(TimeSpan? delta, DateTimeOffset? date) : HttpRequestException("The request was rate limited.", null, HttpStatusCode.TooManyRequests)
{
    public static RateLimitedException From(HttpResponseMessage response) => new(response.Headers.RetryAfter?.Delta, response.Headers.RetryAfter?.Date);
    public static RateLimitedException Held(DateTimeOffset retryAt) => new(null, null) { RetryAt = retryAt, Sent = false };

    public bool Sent { get; private init; } = true;
    public DateTimeOffset? RetryAt { get; set; }
    public TimeSpan? RetryAfter(DateTimeOffset now) => delta ?? (date is { } at ? at - now : null);
}

// The pause after rate limited requests to one account endpoint, for one token. The panel asks about once a minute, which is
// what kept a limited account limited. Without a Retry-After the pause grows 2, 4, 8, 16, then 30 minutes with each further 429
// and ends with the first answer that is not one; only this growth has an upper limit. A Retry-After is the server's own time
// and is kept as sent, however long, raised only to MinDelay; a repeated one that is shorter than the growth does not hold the
// pause down. One longer than MaxPlausibleRetryAfter (a week, the longest window a quota has) is taken for a mistake, such as a
// date far in the future or a count of milliseconds read as seconds, and is treated as missing: a wrong value cannot stop the
// quota for good. A little time (up to JitterShare of it) is added, never taken, so several copies of TwelveIsland behind one
// address do not retry on the same minute.
// The time of the end is kept, not a countdown, so the pause follows the clock it was set with. If the clock is set back, the
// wait left cannot be longer than the pause was decided to be: it is cut to that length from the new time (RetryAt), instead of
// holding requests back for as long as the clock went back.
public sealed class RateLimitBackoff
{
    public static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxGrowth = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaxPlausibleRetryAfter = TimeSpan.FromDays(7);
    public const double JitterShare = 0.1;
    private const int MaxFailures = 16;
    private TimeSpan _wait;

    public int Failures { get; private set; }
    public DateTimeOffset? Until { get; private set; }

    // failures counts this one. jitter is a number from 0 to 1.
    public static TimeSpan Delay(int failures, TimeSpan? retryAfter, double jitter)
    {
        var grown = failures <= 1 ? FirstDelay : TimeSpan.FromTicks((long)Math.Min(FirstDelay.Ticks * Math.Pow(2, Math.Min(failures, MaxFailures) - 1), MaxGrowth.Ticks));
        var delay = grown;
        if (retryAfter is { } told && told <= MaxPlausibleRetryAfter)
        {
            delay = told < MinDelay ? MinDelay : told;
            if (failures > 1 && grown > delay) delay = grown;
        }
        return delay + delay * (JitterShare * Math.Clamp(double.IsNaN(jitter) ? 0 : jitter, 0, 1));
    }

    // When requests may go out again; null when they may now.
    public DateTimeOffset? RetryAt(DateTimeOffset now)
    {
        if (Until is not { } until) return null;
        if (until - now > _wait) Until = until = now + _wait;
        return until > now ? until : null;
    }

    // now is the time the 429 arrived.
    public DateTimeOffset Fail(DateTimeOffset now, TimeSpan? retryAfter, double jitter)
    {
        Failures = Math.Min(Failures + 1, MaxFailures);
        _wait = Delay(Failures, retryAfter, jitter);
        return (Until = now + _wait).Value;
    }

    public void Reset() { Failures = 0; Until = null; _wait = default; }
}
