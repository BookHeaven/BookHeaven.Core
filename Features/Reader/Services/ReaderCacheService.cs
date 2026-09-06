using System.Text.Json;
using System.IO.Compression;
using BookHeaven.Core.Features.Reader.Abstractions;
using BookHeaven.Core.Features.Reader.Models;
using BookHeaven.EbookManager.Abstractions;
using BookHeaven.EbookManager.Entities;
using BookHeaven.EbookManager.Extensions;
using Microsoft.Extensions.Options;

namespace BookHeaven.Core.Features.Reader.Services;

public class ReaderCacheService(
    IEbookManagerProvider ebookManagerProvider,
    IOptions<CoreOptions> options) : IReaderCacheService
{

    private string GetBasePath(Guid bookId) => Path.Combine(options.Value.CachePath, bookId.ToString());
    
    
    public async Task<CachedChapterPages[]> LoadCachedPagesAsync(Guid bookId, string currentReaderSettingsHash)
    {
        var cachePath = Path.Combine(GetBasePath(bookId), "pages.cache");
        if (!File.Exists(cachePath)) return [];
        var cacheContent = await ReadCompressedAsync(cachePath);
        if (cacheContent is null) return [];
        try
        {
            var cache = JsonSerializer.Deserialize<ReaderPagesCache>(cacheContent);
            if (cache is null) return [];
            // If the reader settings hash has changed, we consider the cache useful as a starting point
            // but all chapters remain pending recount
            var isStale = cache.ReaderSettingsHash != currentReaderSettingsHash;
            return cache.CachedPages
                .Select(p => new CachedChapterPages { Pages = p.Pages, PendingRecount = p.PendingRecount || isStale })
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public async Task SaveCachedPagesAsync(Guid bookId, string readerSettingsHash, CachedChapterPages[] pages)
    {
        var cachePath = Path.Combine(GetBasePath(bookId), "pages.cache");
        var cache = new ReaderPagesCache
        {
            ReaderSettingsHash = readerSettingsHash,
            CachedPages = pages.ToList()
        };
        var serialized = JsonSerializer.Serialize(cache);
        await WriteCompressedAsync(cachePath, serialized);
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
        await WriteCompressedAsync(cachePath, contentJson);
    }
    
    public async Task<Content?> LoadCachedContentAsync(Guid bookId)
    {
        var cachePath = Path.Combine(GetBasePath(bookId), "content.cache");
        if (!File.Exists(cachePath)) return null;
        var cacheContent = await ReadCompressedAsync(cachePath);
        if (cacheContent is null) return null;
        try
        {
            var content = JsonSerializer.Deserialize<Content?>(cacheContent);
            return content;
        }
        catch
        {
            return null;
        }
    }
    
    private static async Task WriteCompressedAsync(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var fs = File.Create(path);
        await using var brotli = new BrotliStream(fs, CompressionLevel.Optimal);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        await brotli.WriteAsync(bytes);
    }
    
    private static async Task<string?> ReadCompressedAsync(string path)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            await using var brotli = new BrotliStream(fs, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            await brotli.CopyToAsync(ms);
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return null;
        }
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