using BookHeaven.Domain.Entities;

namespace BookHeaven.Domain.Extensions;

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