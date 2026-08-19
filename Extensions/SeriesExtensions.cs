using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Extensions;

public static class SeriesExtensions
{
    extension(Series series)
    {
        public void UpdateFrom(Series updatedSeries)
        {
            series.Name = updatedSeries.Name;
        }
    }
}