using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> BookLocks = [];

    private const string RegistryFileName = "profile-registry.json";
    private const string PagesFilePrefix = "pages.";

    private string GetBasePath(Guid bookId) => Path.Combine(options.Value.CachePath, bookId.ToString());
    private string GetPagesPath(Guid bookId, string settingsHash) =>
        Path.Combine(GetBasePath(bookId), $"{PagesFilePrefix}{settingsHash}.cache");
    private string GetRegistryPath(Guid bookId) => Path.Combine(GetBasePath(bookId), RegistryFileName);

    public async Task<int[]> LoadCachedPagesAsync(Guid bookId, string readerSettingsHash)
    {
        var cachePath = GetPagesPath(bookId, readerSettingsHash);
        if (!File.Exists(cachePath)) return [];
        var cacheContent = await ReadCompressedAsync(cachePath);
        if (cacheContent is null) return [];
        try
        {
            return JsonSerializer.Deserialize<int[]>(cacheContent) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task SaveCachedPagesAsync(Guid bookId, Guid profileId, string readerSettingsHash, int[] pages)
    {
        var bookLock = GetBookLock(bookId);
        await bookLock.WaitAsync();
        try
        {
            var cachePath = GetPagesPath(bookId, readerSettingsHash);
            await WriteCompressedAsync(cachePath, JsonSerializer.Serialize(pages));

            var registry = await LoadRegistryAsync(bookId);
            registry.Profiles[profileId.ToString()] = readerSettingsHash;
            await SaveRegistryAsync(bookId, registry);

            PruneUnusedPagesFiles(GetBasePath(bookId), registry);
        }
        finally
        {
            bookLock.Release();
        }
    }

    /// <summary>
    /// Removes the profile from every book's registry and deletes any page-count
    /// file left without a profile using it.
    /// </summary>
    public async Task RemoveProfileAsync(Guid profileId)
    {
        var cacheRoot = options.Value.CachePath;
        if (!Directory.Exists(cacheRoot)) return;
        var profileKey = profileId.ToString();

        foreach (var bookDir in Directory.EnumerateDirectories(cacheRoot))
        {
            if (!Guid.TryParse(Path.GetFileName(bookDir), out var bookId)) continue;
            var bookLock = GetBookLock(bookId);
            await bookLock.WaitAsync();
            try
            {
                var registry = await LoadRegistryAsync(bookId);
                if (!registry.Profiles.Remove(profileKey)) continue;
                await SaveRegistryAsync(bookId, registry);
                PruneUnusedPagesFiles(bookDir, registry);
            }
            finally
            {
                bookLock.Release();
            }
        }
    }

    public async Task CacheContentAsync(Guid bookId, string ebookPath)
    {
        var extension = Path.GetExtension(ebookPath).ToLowerInvariant();
        var bookFormat = FormatExtensions.GetFormat(extension);
        var reader = ebookManagerProvider.GetReader(bookFormat);
        var ebook = await reader.ReadAllAsync(ebookPath);
        
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
            return JsonSerializer.Deserialize<Content?>(cacheContent);
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

    private static SemaphoreSlim GetBookLock(Guid bookId) =>
        BookLocks.GetOrAdd(bookId, static _ => new(1, 1));

    private async Task<ReaderCacheRegistry> LoadRegistryAsync(Guid bookId)
    {
        var path = GetRegistryPath(bookId);
        if (!File.Exists(path)) return new();
        try
        {
            var content = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<ReaderCacheRegistry>(content) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private async Task SaveRegistryAsync(Guid bookId, ReaderCacheRegistry registry)
    {
        var path = GetRegistryPath(bookId);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(registry));
    }

    private static void PruneUnusedPagesFiles(string bookDir, ReaderCacheRegistry registry)
    {
        var usedHashes = registry.Profiles.Values.ToHashSet();
        foreach (var file in Directory.EnumerateFiles(bookDir, $"{PagesFilePrefix}*.cache"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var hash = fileName[PagesFilePrefix.Length..];
            if (!usedHashes.Contains(hash)) File.Delete(file);
        }
    }
}
