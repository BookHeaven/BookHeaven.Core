using BookHeaven.Core.Enums;

namespace BookHeaven.Core.Abstractions.Services;

public interface IUrlBuilder
{
    string EbookFilePath(Guid bookId, EbookFormat format);
    string EbookUrl(Guid bookId, EbookFormat format);
    string CoverFilePath(Guid bookId);
    string CoverUrl(Guid bookId);
    string FontFilePath(string fontFamily, string? fontFileName = null);
    string FontUrl(string fontFamily, string fontFileName);
    string DefaultFontDirectory();
}