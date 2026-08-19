using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Extensions;

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
#pragma warning disable CS0618 // Type or member is obsolete
            progress.Page = updatedProgress.Page;
            progress.PageCount = updatedProgress.PageCount;
            progress.PageCountPrev = updatedProgress.PageCountPrev;
            progress.PageCountNext = updatedProgress.PageCountNext;
#pragma warning restore CS0618 // Type or member is obsolete
        }
    }
}