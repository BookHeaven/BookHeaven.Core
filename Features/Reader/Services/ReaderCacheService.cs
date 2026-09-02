using System.Text.Json;
using BookHeaven.Core.Features.Reader.Abstractions;
using BookHeaven.Core.Features.Reader.Models;

namespace BookHeaven.Core.Features.Reader.Services;

public class ReaderCacheService : IReaderCacheService
{
    private Guid _bookId = Guid.Empty;
    private string _cacheDirectory = string.Empty;

    public void Initialize(Guid bookId)
    {
        _bookId = bookId;
        _cacheDirectory = Path.Combine(CoreGlobals.CachePath, _bookId.ToString());
    }

    public async Task<int[]> LoadCachedPagesAsync(string currentReaderSettingsHash)
    {
        if(_bookId == Guid.Empty) throw new InvalidOperationException("You must call Initialize() first.");
        
        var cachePath = Path.Combine(_cacheDirectory, "pages.cache");
        if (!File.Exists(cachePath)) return [];
        var cacheContent = await File.ReadAllTextAsync(cachePath);
        var cache = JsonSerializer.Deserialize<ReaderPagesCache>(cacheContent);
        if (cache is null || cache.ReaderSettingsHash != currentReaderSettingsHash) return [];
        return cache.CachedPages;
    }

    public async Task SaveCachedPagesAsync(string readerSettingsHash, int[] pages)
    {
        if(_bookId == Guid.Empty) throw new InvalidOperationException("You must call Initialize() first.");
        
        var cachePath = Path.Combine(_cacheDirectory, "pages.cache");
        var cache = new ReaderPagesCache
        {
            ReaderSettingsHash = readerSettingsHash,
            CachedPages = pages
        };
        await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(cache));
    }

    public void Dispose()
    {
        _bookId = Guid.Empty;
        _cacheDirectory = string.Empty;
        GC.SuppressFinalize(this);
    }
}