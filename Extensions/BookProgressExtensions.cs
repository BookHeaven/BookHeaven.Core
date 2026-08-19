using BookHeaven.Domain.Entities;

namespace BookHeaven.Domain.Extensions;

public static class BookProgressExtensions
{
    extension(BookProgress progress)
    {
        public string ElapsedTimeFormatted()
        {
            return $"{(int)progress.ElapsedTime.TotalHours} h {progress.ElapsedTime.Minutes:00} m";
        }

        public void UpdateFrom(BookProgress updatedProgress)
        {
            progress.Chapter = updatedProgress.Chapter;
            progress.ChapterProgress = updatedProgress.ChapterProgress;
            progress.Progress = updatedProgress.Progress;
            progress.StartDate = updatedProgress.StartDate;
            progress.EndDate = updatedProgress.EndDate;
            progress.LastRead = updatedProgress.LastRead;
            progress.ElapsedTime = updatedProgress.ElapsedTime;
            progress.BookWordCount = updatedProgress.BookWordCount;
        }
    }
}