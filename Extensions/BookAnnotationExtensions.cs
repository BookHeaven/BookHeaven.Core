using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Extensions;

public static class BookAnnotationExtensions
{
    extension(BookAnnotation annotation)
    {
        public void NormalizeNote() => annotation.Note = annotation.NormalizedNote();
        public string? NormalizedNote() => string.IsNullOrWhiteSpace(annotation.Note) ? null : annotation.Note.Trim();
        
        public bool HasContentChanged(BookAnnotation comparison) =>
            annotation.ChapterIndex != comparison.ChapterIndex
            || annotation.ChapterTitle != comparison.ChapterTitle
            || annotation.StartOffset != comparison.StartOffset
            || annotation.EndOffset != comparison.EndOffset
            || annotation.Text != comparison.Text
            || annotation.Note != comparison.Note;
    }
}