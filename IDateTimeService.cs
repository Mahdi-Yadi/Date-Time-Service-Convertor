public interface IDateTimeService
{
    bool TryParsePersianDate(string persianDate, out DateTimeOffset dtoUtc, string timeZoneId = "UTC");
    DateTimeOffset ConvertToUtc(DateTime localDateTime, string timeZoneId);
    DateTimeOffset ConvertToUtc(DateTimeOffset dateTimeWithOffset);
    string FormatForUser(DateTimeOffset utcDateTime, string timeZoneId, CalendarType calendar, string culture = null);
}
