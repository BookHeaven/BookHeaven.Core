using BookHeaven.Core.Abstractions.Services;
using BookHeaven.Core.Enums;
using BookHeaven.Core.Extensions;
using Microsoft.Extensions.Options;

namespace BookHeaven.Core.Services;

public class UrlBuilder(IOptions<CoreOptions> options) : IUrlBuilder
{
    public string EbookFilePath(Guid bookId, EbookFormat format) => Path.Combine(options.Value.BooksPath, bookId.ToString() + format.GetExtension());

    public string CoverFilePath(Guid bookId) => Path.Combine(options.Value.CoversPath, $"{bookId}.jpg");

    public string EbookUrl(Guid bookId, EbookFormat format) => "/books/" + bookId + format.GetExtension();

    public string CoverUrl(Guid bookId) => "/covers/" + bookId + ".jpg";
    
    public string FontFilePath(string fontFamily, string fontFileName) => Path.Combine(options.Value.FontsPath, fontFamily, fontFileName);

    public string FontUrl(string fontFamily, string fontFileName) => "/fonts/" + fontFamily + "/" + fontFileName;
}