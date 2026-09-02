using BookHeaven.Core.Features.Reader.Models;

namespace BookHeaven.Core.Features.Reader.Abstractions;

public interface IReaderCacheService : IDisposable
{
    void Initialize(Guid bookId);
    Task<int[]> LoadCachedPagesAsync(string readerSettingsHash);
    Task SaveCachedPagesAsync(string readerSettingsHash, int[] pages);
}