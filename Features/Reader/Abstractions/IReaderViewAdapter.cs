using BookHeaven.Core.Features.Reader.Models;

namespace BookHeaven.Core.Features.Reader.Abstractions;

public interface IReaderViewAdapter
{
    Task<ReaderContext> GetReaderContextAsync();
    Task WaitForAssetsToLoadAsync();
    Task RestoreHighlightsAsync();
}
