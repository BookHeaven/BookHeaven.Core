using BookHeaven.Core.DOM.Text.Enums;

namespace BookHeaven.Core.Features.Reader.Models;

public sealed class ReaderContext
{
    public int ViewportWidth { get; init; }
    public int ViewportHeight { get; init; }
    public int PageWidth { get; init; }
    public int PageHeight { get; init; }
    public LineBreakingMode LineBreakingMode { get; init; } = LineBreakingMode.Chrome;
    public int ColumnCount { get; init; } = 1;

    public static ReaderContext FromBrowserInfo(ReaderContextInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        return new ReaderContext
        {
            ViewportWidth = info.ViewportWidth,
            ViewportHeight = info.ViewportHeight,
            PageWidth = (int)info.PageWidth,
            PageHeight = (int)info.PageHeight,
            LineBreakingMode = info.Engine == "gecko" ? LineBreakingMode.Firefox : LineBreakingMode.Chrome,
            ColumnCount = info.ColumnCount
        };
    }
}

public sealed record ReaderContextInfo(
    int ViewportWidth,
    int ViewportHeight,
    double PageWidth,
    double PageHeight,
    string Engine,
    int ColumnCount);