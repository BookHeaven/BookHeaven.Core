using System.Text.Json;
using BookHeaven.Core.Features.Reader.Abstractions;
using BookHeaven.Core.Features.Reader.Models;
using BookHeaven.EbookManager;
using BookHeaven.EbookManager.Entities;
using BookHeaven.EbookManager.Extensions;
using BookHeaven.EbookManager.Formats;

namespace BookHeaven.Core.Features.Reader.Services;

public class ReaderCacheService(IEbookManagerProvider ebookManagerProvider) : IReaderCacheService
{

    private static string GetBasePath(Guid bookId) => Path.Combine(EbookManagerGlobals.CachePath, bookId.ToString());
    
    
    public async Task<int[]> LoadCachedPagesAsync(Guid bookId, string currentReaderSettingsHash)
    {
        var cachePath = Path.Combine(GetBasePath(bookId), "pages.cache");
        if (!File.Exists(cachePath)) return [];
        var cacheContent = await File.ReadAllTextAsync(cachePath);
        var cache = JsonSerializer.Deserialize<ReaderPagesCache>(cacheContent);
        if (cache is null || cache.ReaderSettingsHash != currentReaderSettingsHash) return [];
        return cache.CachedPages;
    }

    public async Task SaveCachedPagesAsync(Guid bookId, string readerSettingsHash, int[] pages)
    {
        var cachePath = Path.Combine(GetBasePath(bookId), "pages.cache");
        var cache = new ReaderPagesCache
        {
            ReaderSettingsHash = readerSettingsHash,
            CachedPages = pages
        };
        await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(cache));
    }

    public async Task CacheContentAsync(Guid bookId, string ebookPath)
    {
        var extension = Path.GetExtension(ebookPath).ToLowerInvariant();
        var bookFormat = FormatExtensions.GetFormat(extension);
        var reader = ebookManagerProvider.GetReader(bookFormat);
        var ebook = await reader.ReadAllAsync(ebookPath);
        reader.Dispose();
        
        await CacheContentAsync(bookId, ebook.Content);
    }
    
    public async Task CacheContentAsync(Guid bookId, Content content)
    {
        var cachePath = Path.Combine(GetBasePath(bookId), "content.cache");
        var contentJson = JsonSerializer.Serialize(content);
        await File.WriteAllTextAsync(cachePath, contentJson);
    }
    
    public async Task<Content?> LoadCachedContentAsync(Guid bookId)
    {
        var cachePath = Path.Combine(GetBasePath(bookId), "content.cache");
        if (!File.Exists(cachePath)) return null;
        var cacheContent = await File.ReadAllTextAsync(cachePath);
        var content = JsonSerializer.Deserialize<Content?>(cacheContent);
        return content;
    }
    
    public void ClearCache(Guid bookId)
    {
        var path = GetBasePath(bookId);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}