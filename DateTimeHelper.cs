using System;

namespace RawThumbnailExtractor
{
    public static class DateTimeHelper
    {
        public static DateTime FromUnixTime(this int unixTime)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddSeconds(unixTime);
        }
    }
}
