using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace MyApp.Infrastructure.Time
{
    /// <summary>
    /// Supported calendar systems for formatting and parsing.
    /// </summary>
    public enum CalendarType
    {
        Gregorian,
        Persian,
        Hijri,
        Other
    }

    /// <summary>
    /// Synchronous DateTime service interface.
    /// Provides parsing, formatting and timezone-aware conversions.
    /// Designed to be stateless and thread-safe.
    /// </summary>
    public interface IDateTimeService
    {
        /// <summary>
        /// Try to parse an input string that represents a Persian date (with optional time)
        /// and convert it to a UTC DateTimeOffset. Returns false on failure.
        /// </summary>
        bool TryParsePersianDate(string persianDate, out DateTimeOffset dtoUtc, string timeZoneId = "UTC");

        /// <summary>
        /// Try to parse a date string that may be Persian, Gregorian or Hijri (auto-detect),
        /// returning UTC if successful. This is a best-effort convenience method.
        /// </summary>
        bool TryParseAny(string inputDate, out DateTimeOffset dtoUtc, string timeZoneId = "UTC");

        /// <summary>
        /// Convert a local DateTime (interpreted in the provided time zone) into UTC.
        /// </summary>
        DateTimeOffset ConvertToUtc(DateTime localDateTime, string timeZoneId);

        /// <summary>
        /// Convert a DateTimeOffset (with offset) into UTC.
        /// </summary>
        DateTimeOffset ConvertToUtc(DateTimeOffset dateTimeWithOffset);

        /// <summary>
        /// Convert a UTC DateTimeOffset into the user's time zone and format using the chosen calendar.
        /// </summary>
        string FormatForUser(DateTimeOffset utcDateTime, string timeZoneId, CalendarType calendar, string culture = null);

        /// <summary>
        /// Normalize an incoming date string: convert Persian/Arabic digits to latin digits,
        /// standardize separators, and optionally strip non-date characters.
        /// </summary>
        string NormalizeForDate(string input);

        /// <summary>
        /// A small humanization helper (English) for relative times, e.g. "2 days ago", "in 3 hours".
        /// This is intentionally simple and not localization-heavy — extend as needed.
        /// </summary>
        string HumanizeRelative(DateTimeOffset utcDateTime, DateTimeOffset nowUtc);

        /// <summary>
        /// Validate that a timezone id is recognized by the runtime (returns true if found).
        /// Caution: on different OSes timezone ids differ (Windows vs IANA).
        /// </summary>
        bool TryGetTimeZone(string timeZoneId, out TimeZoneInfo tzInfo);
    }

    /// <summary>
    /// Optional lightweight async wrapper interface.
    /// Useful if you want all DI-injected services to be async-capable.
    /// Internally the implementation can use Task.FromResult as operations are CPU-bound.
    /// </summary>
    public interface IDateTimeServiceAsync
    {
        Task<(bool success, DateTimeOffset dtoUtc)> TryParsePersianDateAsync(string persianDate, string timeZoneId = "UTC");
        Task<(bool success, DateTimeOffset dtoUtc)> TryParseAnyAsync(string inputDate, string timeZoneId = "UTC");
        Task<DateTimeOffset> ConvertToUtcAsync(DateTime localDateTime, string timeZoneId);
        Task<DateTimeOffset> ConvertToUtcAsync(DateTimeOffset dateTimeWithOffset);
        Task<string> FormatForUserAsync(DateTimeOffset utcDateTime, string timeZoneId, CalendarType calendar, string culture = null);
        Task<string> NormalizeForDateAsync(string input);
        Task<string> HumanizeRelativeAsync(DateTimeOffset utcDateTime, DateTimeOffset nowUtc);
        Task<(bool ok, TimeZoneInfo tz)> TryGetTimeZoneAsync(string timeZoneId);
    }

    /// <summary>
    /// Full-featured DateTimeService.
    /// - Uses static calendar instances to avoid allocations.
    /// - Precompiles regex for parsing.
    /// - Caches TimeZoneInfo lookups via ConcurrentDictionary.
    /// - Provides normalization for Persian/Arabic digits.
    /// - Thread-safe and intended to be registered as Singleton.
    /// </summary>
    public class DateTimeService : IDateTimeService
    {
        // Reused calendar instances (thread-safe for reads)
        private static readonly PersianCalendar _persianCalendar = new PersianCalendar();
        private static readonly HijriCalendar _hijriCalendar = new HijriCalendar();

        // Precompiled regex for parsing dates and optional times:
        // Matches: yyyy/MM/dd [HH:mm[:ss]]  (separators may be /, -, . or whitespace)
        private static readonly Regex _dateTimeRegex;

        // Cache for TimeZoneInfo resolution to avoid expensive repeated lookups
        private static readonly ConcurrentDictionary<string, TimeZoneInfo> _tzCache = new();

        // static ctor builds the regex once
        static DateTimeService()
        {
            _dateTimeRegex = new Regex(
                @"^\s*(\d{2,4})[\/\-\.\s](\d{1,2})[\/\-\.\s](\d{1,2})(?:\s+(\d{1,2}):(\d{1,2})(?::(\d{1,2}))?)?\s*$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        }

        /// <summary>
        /// Get TimeZoneInfo from cache or resolve and cache it. Falls back to UTC on failure.
        /// This method is thread-safe.
        /// </summary>
        private TimeZoneInfo GetTimeZoneOrUtc(string timeZoneId)
        {
            if (string.IsNullOrWhiteSpace(timeZoneId))
                return TimeZoneInfo.Utc;

            return _tzCache.GetOrAdd(timeZoneId, id =>
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch { return TimeZoneInfo.Utc; }
            });
        }

        /// <inheritdoc />
        public bool TryParsePersianDate(string persianDate, out DateTimeOffset dtoUtc, string timeZoneId = "UTC")
        {
            dtoUtc = default;
            if (string.IsNullOrWhiteSpace(persianDate)) return false;

            // 1) Normalize digits and strip noise
            var normalized = PersianDigitsNormalizer.NormalizeForDate(persianDate);
            if (string.IsNullOrWhiteSpace(normalized)) return false;

            // 2) Match with precompiled regex
            var m = _dateTimeRegex.Match(normalized);
            if (!m.Success) return false;

            // 3) Parse components in invariant culture
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int year)) return false;
            if (!int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int month)) return false;
            if (!int.TryParse(m.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int day)) return false;

            int hour = 0, minute = 0, second = 0;
            if (m.Groups[4].Success)
            {
                if (!int.TryParse(m.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)) return false;
                if (!int.TryParse(m.Groups[5].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out minute)) return false;
                if (m.Groups[6].Success)
                    int.TryParse(m.Groups[6].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out second);
            }

            try
            {
                // Convert Persian date parts to a DateTime using PersianCalendar.
                // Note: DateTimeKind.Unspecified is used so timezone offset calculation is explicit.
                var dt = _persianCalendar.ToDateTime(year, month, day, hour, minute, second, 0, DateTimeKind.Unspecified);

                var tz = GetTimeZoneOrUtc(timeZoneId);
                var offset = tz.GetUtcOffset(dt);
                dtoUtc = new DateTimeOffset(dt, offset).ToUniversalTime();
                return true;
            }
            catch
            {
                // Parsing failure or invalid Persian date (e.g., month/day out of range)
                return false;
            }
        }

        /// <inheritdoc />
        public bool TryParseAny(string inputDate, out DateTimeOffset dtoUtc, string timeZoneId = "UTC")
        {
            dtoUtc = default;
            if (string.IsNullOrWhiteSpace(inputDate)) return false;

            // First try Persian-style parsing
            if (TryParsePersianDate(inputDate, out dtoUtc, timeZoneId)) return true;

            // Next, normalize digits and try a general ISO / culture-invariant parse
            var normalized = PersianDigitsNormalizer.Normalize(inputDate);

            // Try parse as DateTimeOffset (ISO 8601 or with offset)
            if (DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            {
                dtoUtc = dto.ToUniversalTime();
                return true;
            }

            // Try parse as local DateTime and convert using supplied timezone
            if (DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtLocal))
            {
                dtoUtc = ConvertToUtc(dtLocal, timeZoneId);
                return true;
            }

            // As a last resort, attempt to match numeric date/time patterns (yyyy/MM/dd ...)
            var m = _dateTimeRegex.Match(PersianDigitsNormalizer.NormalizeForDate(inputDate));
            if (m.Success)
            {
                // Attempt to interpret as Gregorian by direct ToDateTime
                if (int.TryParse(m.Groups[1].Value, out int y) &&
                    int.TryParse(m.Groups[2].Value, out int mo) &&
                    int.TryParse(m.Groups[3].Value, out int d))
                {
                    int hr = 0, min = 0, sec = 0;
                    if (m.Groups[4].Success)
                    {
                        int.TryParse(m.Groups[4].Value, out hr);
                        int.TryParse(m.Groups[5].Value, out min);
                        if (m.Groups[6].Success) int.TryParse(m.Groups[6].Value, out sec);
                    }

                    try
                    {
                        var dt = new DateTime(y, mo, d, hr, min, sec, DateTimeKind.Unspecified);
                        dtoUtc = ConvertToUtc(dt, timeZoneId);
                        return true;
                    }
                    catch { /* ignore */ }
                }
            }

            return false;
        }

        /// <inheritdoc />
        public DateTimeOffset ConvertToUtc(DateTime localDateTime, string timeZoneId)
        {
            var tz = GetTimeZoneOrUtc(timeZoneId);
            var unspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
            var offset = tz.GetUtcOffset(unspecified);
            return new DateTimeOffset(unspecified, offset).ToUniversalTime();
        }

        /// <inheritdoc />
        public DateTimeOffset ConvertToUtc(DateTimeOffset dateTimeWithOffset)
        {
            return dateTimeWithOffset.ToUniversalTime();
        }

        /// <inheritdoc />
        public string FormatForUser(DateTimeOffset utcDateTime, string timeZoneId, CalendarType calendar, string culture = null)
        {
            var tz = GetTimeZoneOrUtc(timeZoneId);
            var userTime = TimeZoneInfo.ConvertTime(utcDateTime, tz);

            culture ??= (calendar == CalendarType.Persian ? "fa-IR" : "en-US");

            if (calendar == CalendarType.Persian)
            {
                var dt = userTime.DateTime;
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:0000}/{1:00}/{2:00} {3:00}:{4:00}:{5:00}",
                    _persianCalendar.GetYear(dt),
                    _persianCalendar.GetMonth(dt),
                    _persianCalendar.GetDayOfMonth(dt),
                    dt.Hour, dt.Minute, dt.Second);
            }
            else if (calendar == CalendarType.Hijri)
            {
                var dt = userTime.DateTime;
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:0000}/{1:00}/{2:00} {3:00}:{4:00}:{5:00}",
                    _hijriCalendar.GetYear(dt),
                    _hijriCalendar.GetMonth(dt),
                    _hijriCalendar.GetDayOfMonth(dt),
                    dt.Hour, dt.Minute, dt.Second);
            }
            else
            {
                // ISO-style representation for Gregorian calendar
                return userTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
        }

        /// <inheritdoc />
        public string NormalizeForDate(string input)
        {
            return PersianDigitsNormalizer.NormalizeForDate(input);
        }

        /// <inheritdoc />
        public string HumanizeRelative(DateTimeOffset utcDateTime, DateTimeOffset nowUtc)
        {
            // Simple humanizer (English). Expand or localize as needed.
            var span = nowUtc - utcDateTime;
            var future = span.TotalSeconds < 0;
            var delta = Math.Abs(span.TotalSeconds);

            if (delta < 60) return future ? "in a few seconds" : "just now";
            if (delta < 3600)
            {
                var m = (int)Math.Round(delta / 60);
                return future ? $"in {m} minute{(m > 1 ? "s" : "")}" : $"{m} minute{(m > 1 ? "s" : "")} ago";
            }
            if (delta < 86400)
            {
                var h = (int)Math.Round(delta / 3600);
                return future ? $"in {h} hour{(h > 1 ? "s" : "")}" : $"{h} hour{(h > 1 ? "s" : "")} ago";
            }
            var days = (int)Math.Round(delta / 86400);
            return future ? $"in {days} day{(days > 1 ? "s" : "")}" : $"{days} day{(days > 1 ? "s" : "")} ago";
        }

        /// <inheritdoc />
        public bool TryGetTimeZone(string timeZoneId, out TimeZoneInfo tzInfo)
        {
            try
            {
                tzInfo = GetTimeZoneOrUtc(timeZoneId);
                return tzInfo != null;
            }
            catch
            {
                tzInfo = TimeZoneInfo.Utc;
                return false;
            }
        }
    }

    /// <summary>
    /// Async wrapper that exposes the same behavior as DateTimeService but via Task-based methods.
    /// Methods are thin wrappers (Task.FromResult) because operations are CPU-bound and fast.
    /// This wrapper is useful to keep DI consumers uniformly async.
    /// </summary>
    public class DateTimeServiceAsyncWrapper : IDateTimeServiceAsync
    {
        private readonly IDateTimeService _inner;
        public DateTimeServiceAsyncWrapper(IDateTimeService inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        public Task<(bool success, DateTimeOffset dtoUtc)> TryParsePersianDateAsync(string persianDate, string timeZoneId = "UTC")
        {
            var ok = _inner.TryParsePersianDate(persianDate, out var dto, timeZoneId);
            return Task.FromResult((ok, dto));
        }

        public Task<(bool success, DateTimeOffset dtoUtc)> TryParseAnyAsync(string inputDate, string timeZoneId = "UTC")
        {
            var ok = _inner.TryParseAny(inputDate, out var dto, timeZoneId);
            return Task.FromResult((ok, dto));
        }

        public Task<DateTimeOffset> ConvertToUtcAsync(DateTime localDateTime, string timeZoneId)
        {
            return Task.FromResult(_inner.ConvertToUtc(localDateTime, timeZoneId));
        }

        public Task<DateTimeOffset> ConvertToUtcAsync(DateTimeOffset dateTimeWithOffset)
        {
            return Task.FromResult(_inner.ConvertToUtc(dateTimeWithOffset));
        }

        public Task<string> FormatForUserAsync(DateTimeOffset utcDateTime, string timeZoneId, CalendarType calendar, string culture = null)
        {
            return Task.FromResult(_inner.FormatForUser(utcDateTime, timeZoneId, calendar, culture));
        }

        public Task<string> NormalizeForDateAsync(string input)
        {
            return Task.FromResult(_inner.NormalizeForDate(input));
        }

        public Task<string> HumanizeRelativeAsync(DateTimeOffset utcDateTime, DateTimeOffset nowUtc)
        {
            return Task.FromResult(_inner.HumanizeRelative(utcDateTime, nowUtc));
        }

        public Task<(bool ok, TimeZoneInfo tz)> TryGetTimeZoneAsync(string timeZoneId)
        {
            var ok = _inner.TryGetTimeZone(timeZoneId, out var tz);
            return Task.FromResult((ok, tz));
        }
    }

    /// <summary>
    /// Utility class that normalizes Persian/Arabic digits to Latin digits and
    /// cleans common separators so parsing becomes reliable.
    /// Keep this static and lightweight. You can extend it to handle more exotic inputs.
    /// </summary>
    public static class PersianDigitsNormalizer
    {
        private static readonly char[] PersianDigits = new char[] { '۰', '۱', '۲', '۳', '۴', '۵', '۶', '۷', '۸', '۹' };
        private static readonly char[] ArabicIndicDigits = new char[] { '٠', '١', '٢', '٣', '٤', '٥', '٦', '٧', '٨', '٩' };

        /// <summary>
        /// Convert Persian/Arabic digits to ASCII digits and normalize common separators.
        /// Does not aggressively remove text; it attempts to preserve digits & separators.
        /// </summary>
        public static string Normalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return input;
            var sb = new StringBuilder(input.Length);

            foreach (var ch in input)
            {
                // Persian digits
                int idx = Array.IndexOf(PersianDigits, ch);
                if (idx >= 0) { sb.Append((char)('0' + idx)); continue; }

                // Arabic-Indic digits
                idx = Array.IndexOf(ArabicIndicDigits, ch);
                if (idx >= 0) { sb.Append((char)('0' + idx)); continue; }

                // Normalize decimal separators and commas
                if (ch == '٫' || ch == ',' || ch == '٬') { sb.Append('.'); continue; }

                // Normalize forward/back slashes and various dash characters to '/'
                if (ch == '／' || ch == '/' || ch == '╱') { sb.Append('/'); continue; }
                if (ch == '-' || ch == '–' || ch == '—' || ch == '-') { sb.Append('-'); continue; }

                // Normalize zero-width and special spaces to normal space
                if (char.IsWhiteSpace(ch) || ch == '\u200C') { sb.Append(' '); continue; }

                // Keep ASCII characters as-is
                if (ch <= 127) { sb.Append(ch); continue; }

                // Otherwise preserve character (this helps when input includes "ساعت" or similar)
                sb.Append(ch);
            }

            // Collapse multiple spaces and trim
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        /// <summary>
        /// Strict normalizer tailored for date parsing: keeps only digits and / - . and spaces.
        /// Useful as a pre-step before regex matching.
        /// </summary>
        public static string NormalizeForDate(string input)
        {
            var n = Normalize(input);
            // Keep only digits, separators and spaces
            return Regex.Replace(n, @"[^\d\/\-\.\s:]", "");
        }
    }

    /// <summary>
    /// DI registration helpers for convenience.
    /// Call builder.Services.AddDateTimeServices() from Program.cs.
    /// </summary>
    public static class DateTimeServiceExtensions
    {
        /// <summary>
        /// Registers the DateTime service and its async wrapper.
        /// DateTimeService is registered as Singleton (thread-safe, stateless).
        /// </summary>
        public static IServiceCollection AddDateTimeServices(this IServiceCollection services)
        {
            services.AddSingleton<IDateTimeService, DateTimeService>();
            services.AddSingleton<IDateTimeServiceAsync, DateTimeServiceAsyncWrapper>(sp =>
            {
                var inner = sp.GetRequiredService<IDateTimeService>();
                return new DateTimeServiceAsyncWrapper(inner);
            });

            return services;
        }
    }

    /* Usage Notes (quick):
     *
     * - Register:
     *     builder.Services.AddDateTimeServices();
     *
     * - Parse Persian input from UI and store UTC:
     *     if (dateService.TryParsePersianDate("۱۴۰۳/۰۳/۰۱ 12:30", out var utc, "Asia/Tehran"))
     *     {
     *         entity.Date = utc; // store this in DB as UTC (DateTimeOffset)
     *     }
     *
     * - Format for a user:
     *     var display = dateService.FormatForUser(utc, "Asia/Tehran", CalendarType.Persian);
     *
     * - Use TryParseAny to accept multiple input styles:
     *     dateService.TryParseAny("2025-03-01T12:30:00+03:00", out var u, "UTC");
     *
     * - Humanize:
     *     dateService.HumanizeRelative(someUtc, DateTimeOffset.UtcNow);
     *
     * - For production-grade timezone handling and absolute correctness in multi-region systems,
     *   consider replacing parts with NodaTime (recommended) — this implementation is optimized
     *   and robust, but NodaTime handles edge cases and DST/zone differences more thoroughly.
     */
}
