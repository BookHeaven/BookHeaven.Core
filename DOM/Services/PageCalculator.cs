using BookHeaven.Core.Abstractions.Services;
using BookHeaven.Core.DOM.Engine;
using BookHeaven.Core.DOM.Services.Abstractions;
using BookHeaven.EbookManager;
using BookHeaven.EbookManager.Entities;
using Mediator;
using Microsoft.Extensions.Options;

namespace BookHeaven.Core.DOM.Services;

/// <summary>
/// High-level entry point for page-map calculation. Uses the BlockLayoutEngine and
/// BlockPageSplitter (block-based path, see BLOCK_LAYOUT_PLAN.md) to count pages from
/// HTML + optional CSS. The legacy line-based engine (DomLayoutEngine + PageSplitter)
/// remains in the codebase untouched while the block path is being validated.
///
/// Chapters of a book are layout-independent, so they are processed IN PARALLEL
/// (up to <see cref="MaxParallelChapters"/> at a time): one engine per chapter
/// (engine state is per-document), while the thread-safe text measurer is shared
/// across all engines so the HarfBuzz word-shaping cache stays warm book-wide.
/// Results are written to a per-chapter index, so the returned array is identical
/// to what a sequential run would produce.
/// </summary>
public sealed class PageCalculator(IOptions<CoreOptions> coreOptions, ISender? sender = null, IUrlBuilder? urlBuilder = null) : IPageCalculator
{
    /// <summary>
    /// Upper bound on the number of chapters being laid out simultaneously.
    /// Measuring is CPU-bound; more parallelism than this neither speeds the
    /// book up (it is CPU-bound, not IO-bound) nor is it worth the cache
    /// contention on the shared measurer.
    /// </summary>
    private const int MaxParallelChapters = 4;

    /// <summary>
    /// Convenience API for callers that have an `Ebook` instance.
    /// Builds HTML and CSS from the ebook's content and returns an array of page start offsets (pixels).
    /// </summary>
    public async Task<int[]> CalculatePagesAsync(Ebook ebook, PageCalculatorOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ebook);
        var chapters = ebook.Content.Chapters;

        var results = new int[chapters.Count];
        var normalized = options.Normalize();

        // Chapter CSS depends only on the chapter, so build it all up front
        // (it used to be rebuilt inside the loop, once per chapter).
        var chapterCss = new Dictionary<int, List<string>>();
        var stylesheets = ebook.Content.Stylesheets;
        for (var i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            var styles = new List<string>();

            if(!string.IsNullOrEmpty(chapter.ParagraphClassName))
            {
                styles.Add($"body p.{chapter.ParagraphClassName} {{ margin-block: calc(var(--paragraph-spacing) * 1pt) !important; }}");
            }

            foreach (var stylesheet in stylesheets)
            {
                if(chapter.Stylesheets.Contains(stylesheet.Identifier))
                {
                    styles.Add(stylesheet.Content);
                }
            }

            chapterCss[i] = styles;
        }

        // One shared, thread-safe measurer for the whole book (native resources
        // are released below, when the book is done); each parallel chapter gets
        // its own engine (per-document state) but reuses that measurer.
        // With a mediator available, the per-style variants of the selected font
        // family are resolved from the database; otherwise the sync path (default
        // font file only) is used.
        var sharedMeasurer = sender != null && urlBuilder != null
            ? await BlockLayoutEngine.CreateMeasurerAsync(normalized, sender, urlBuilder, cancellationToken)
            : BlockLayoutEngine.CreateMeasurer(normalized);
        try
        {
            using var throttled = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount, 1, MaxParallelChapters));

            await Task.WhenAll(chapters.Select((chapter, i) => Task.Run(async () =>
            {
                await throttled.WaitAsync(cancellationToken);
                try
                {
                    // Generate layout blocks and split them into pages (block-based path).
                    using var engine = new BlockLayoutEngine(normalized, coreOptions, sharedMeasurer);
                    var blocks = await engine.GenerateLayoutBlocksAsync(chapter.Content, chapterCss[i], cancellationToken);
                    var map = BlockPageSplitter.SplitToPages(blocks, normalized.PageHeightPx);

                    // Indexed write: output is identical to a sequential run, no locking needed.
                    results[i] = map.Pages.Count;
                }
                finally
                {
                    throttled.Release();
                }
            }, cancellationToken)));
        }
        finally
        {
            // HarfBuzz/Skia measurers hold native resources; ITextMeasurer does not
            // (deliberately) declare IDisposable, so dispose it here, once, explicitly.
            (sharedMeasurer as IDisposable)?.Dispose();
        }

        return results;
    }
}
