using BookHeaven.Core.Features.Reader.Models;
using BookHeaven.EbookManager.Entities;
using BookHeaven.EbookManager.Enums;

namespace BookHeaven.Core.Features.Reader.Abstractions;

public interface IReaderCacheService
{
    Task<CachedChapterPages[]> LoadCachedPagesAsync(Guid bookId, string readerSettingsHash);
    Task SaveCachedPagesAsync(Guid bookId, string readerSettingsHash, CachedChapterPages[] pages);
    Task CacheContentAsync(Guid bookId, string ebookPath);
    Task CacheContentAsync(Guid bookId, Content content);
    Task<Content?> LoadCachedContentAsync(Guid bookId);
    void ClearCache(Guid bookId);
}