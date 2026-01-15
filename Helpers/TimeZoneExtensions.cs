using System;

namespace NextStakeWebApp.Helpers
{
    public static class TimeZoneExtensions
    {
        private static readonly TimeZoneInfo RomeTz = GetRomeTimeZone();

        private static TimeZoneInfo GetRomeTimeZone()
        {
            // Linux (Render): "Europe/Rome"
            // Windows (dev): "W. Europe Standard Time"
            try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome"); }
            catch
            {
                return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
            }
        }

        public static DateTime ToRomeTime(this DateTime utc)
        {
            // Se dal DB arriva Kind=Unspecified, lo forziamo a UTC perché i tuoi campi sono *Utc*
            var asUtc = utc.Kind == DateTimeKind.Utc
                ? utc
                : DateTime.SpecifyKind(utc, DateTimeKind.Utc);

            return TimeZoneInfo.ConvertTimeFromUtc(asUtc, RomeTz);
        }

        public static DateTime? ToRomeTime(this DateTime? utc)
        {
            if (!utc.HasValue) return null;
            return utc.Value.ToRomeTime();
        }
    }
}
