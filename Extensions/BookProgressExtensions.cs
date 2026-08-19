using BookHeaven.Domain.Entities;

namespace BookHeaven.Domain.Extensions;

public static class BookProgressExtensions
{
    public static string ElapsedTimeFormatted(this BookProgress progress)
    {
        return $"{(int)progress.ElapsedTime.TotalHours} h {progress.ElapsedTime.Minutes:00} m";
    extension(BookProgress progress)
    {
        public string ElapsedTimeFormatted()
        {
            return $"{(int)progress.ElapsedTime.TotalHours} h {progress.ElapsedTime.Minutes:00} m";
        }
    }
}