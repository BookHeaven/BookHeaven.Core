namespace BookHeaven.Core.Features.Reader.Models;

/// <summary>
/// Maps profile IDs to the reader settings hash each profile currently uses,
/// so the cache can tell which page-count files are still needed.
/// </summary>
public class ReaderCacheRegistry
{
    public Dictionary<string, string> Profiles { get; set; } = [];
}
