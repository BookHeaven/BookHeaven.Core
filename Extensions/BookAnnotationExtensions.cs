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

        public bool Overlaps(int startOffset, int endOffset) =>
            annotation.StartOffset < endOffset && annotation.EndOffset > startOffset;

        public void CopyContentFrom(BookAnnotation source)
        {
            annotation.ChapterIndex = source.ChapterIndex;
            annotation.ChapterTitle = source.ChapterTitle;
            annotation.StartOffset = source.StartOffset;
            annotation.EndOffset = source.EndOffset;
            annotation.Text = source.Text;
            annotation.Note = source.Note;
        }

        public BookAnnotation Clone() => new()
        {
            BookAnnotationId = annotation.BookAnnotationId,
            BookId = annotation.BookId,
            ProfileId = annotation.ProfileId,
            ChapterIndex = annotation.ChapterIndex,
            ChapterTitle = annotation.ChapterTitle,
            StartOffset = annotation.StartOffset,
            EndOffset = annotation.EndOffset,
            Text = annotation.Text,
            Note = annotation.Note,
            UpdatedAt = annotation.UpdatedAt
        };

        public BookAnnotation WithNote(string? note)
        {
            var copy = annotation.Clone();
            copy.Note = note;
            return copy;
        }

        public BookAnnotation WithRange(int? startOffset = null, int? endOffset = null, string? text = null)
        {
            var copy = annotation.Clone();
            if (startOffset is not null) copy.StartOffset = startOffset.Value;
            if (endOffset is not null) copy.EndOffset = endOffset.Value;
            if (text is not null) copy.Text = text;
            return copy;
        }
    }
}