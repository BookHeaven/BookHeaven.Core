namespace BookHeaven.Core.Extensions;

public static class TimeSpanExtensions
{
    extension(TimeSpan timeSpan)
    {
        public string ToDisplayFormat()
        {
            return $"{(int)timeSpan.TotalHours} h {timeSpan.Minutes:00} m";
        }
    }
}