using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
public class DateTimeService : IDateTimeService
{
    // Static calendar instances (thread-safe and avoid repeated allocations)
    private static readonly PersianCalendar _persianCalendar = new PersianCalendar();
    private static readonly HijriCalendar _hijriCalendar = new HijriCalendar();

    // Precompiled regex for high-performance date/time parsing
    private static readonly Regex _dateTimeRegex;

    // Cache for resolved TimeZoneInfo instances to avoid expensive lookups
    private static readonly ConcurrentDictionary<string, TimeZoneInfo> _tzCache = new();

    // Static constructor initializes precompiled regex once
    static DateTimeService()
    {
        // Matches formats like:
        // yyyy/MM/dd
        // yyyy-MM-dd
        // yyyy.MM.dd
        // with optional time: HH:mm[:ss]
        _dateTimeRegex = new Regex(
            @"^\s*(\d{2,4})[\/\-\.\s](\d{1,2})[\/\-\.\s](\d{1,2})(?:\s+(\d{1,2}):(\d{1,2})(?::(\d{1,2}))?)?\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    /// <summary>
    /// Retrieves a TimeZoneInfo instance from cache or resolves it once and stores it.
    /// If resolution fails, falls back to UTC.
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

    /// <summary>
    /// Attempts to parse a Persian date string (with optional time) and convert it into UTC.
    /// Returns true if parsing succeeds, false otherwise.
    /// </summary>
    public bool TryParsePersianDate(string persianDate, out DateTimeOffset dtoUtc, string timeZoneId = "UTC")
    {
        dtoUtc = default;

        if (string.IsNullOrWhiteSpace(persianDate))
            return false;

        // Normalize digits (Persian/Arabic → Latin) and remove noise
        var normalized = PersianDigitsNormalizer.NormalizeForDate(persianDate);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        // Match normalized input against the regex
        var m = _dateTimeRegex.Match(normalized);
        if (!m.Success)
            return false;

        // Extract date components
        if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int year)) return false;
        if (!int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int month)) return false;
        if (!int.TryParse(m.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int day)) return false;

        // Extract optional time components
        int hour = 0, minute = 0, second = 0;
        if (m.Groups[4].Success)
        {
            if (!int.TryParse(m.Groups[4].Value, out hour)) return false;
            if (!int.TryParse(m.Groups[5].Value, out minute)) return false;
            if (m.Groups[6].Success) int.TryParse(m.Groups[6].Value, out second);
        }

        try
        {
            // Convert Persian calendar date to DateTime (unspecified kind)
            var dt = _persianCalendar.ToDateTime(
                year, month, day, hour, minute, second, 0, (int)DateTimeKind.Unspecified);

            // Apply timezone offset and convert to UTC
            var tz = GetTimeZoneOrUtc(timeZoneId);
            var offset = tz.GetUtcOffset(dt);

            dtoUtc = new DateTimeOffset(dt, offset).ToUniversalTime();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Converts a local DateTime (interpreted in the given timezone) into UTC.
    /// </summary>
    public DateTimeOffset ConvertToUtc(DateTime localDateTime, string timeZoneId)
    {
        var tz = GetTimeZoneOrUtc(timeZoneId);

        // Ensure that DateTime does not carry an unintended Kind value
        var unspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);

        var offset = tz.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }

    /// <summary>
    /// Converts a DateTimeOffset with any offset into UTC.
    /// </summary>
    public DateTimeOffset ConvertToUtc(DateTimeOffset dateTimeWithOffset)
    {
        return dateTimeWithOffset.ToUniversalTime();
    }

    /// <summary>
    /// Formats a UTC DateTimeOffset into user-friendly text using the user's timezone
    /// and the selected calendar system (Persian, Hijri, Gregorian).
    /// </summary>
    public string FormatForUser(DateTimeOffset utcDateTime, string timeZoneId, CalendarType calendar, string culture = null)
    {
        var tz = GetTimeZoneOrUtc(timeZoneId);

        // Convert UTC time to user's timezone
        var userTime = TimeZoneInfo.ConvertTime(utcDateTime, tz);

        // Default culture fallback
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
            // ISO style formatting for Gregorian calendar
            return userTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }
}
