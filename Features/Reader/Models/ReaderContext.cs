using BookHeaven.Core.DOM.Text.Enums;

namespace BookHeaven.Core.Features.Reader.Models;

public class ReaderContext
{
    public int ViewportWidth { get; init; }
    public int ViewportHeight { get; init; }
    public int PageWidth { get; init; }
    public int PageHeight { get; init; }
    public LineBreakingMode LineBreakingMode { get; init; } = LineBreakingMode.Chrome;
    public int ColumnCount { get; init; } = 1;
}