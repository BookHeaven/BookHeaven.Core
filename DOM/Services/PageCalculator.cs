using System.Threading.Channels;
using BookHeaven.Core.Abstractions.Services;
using BookHeaven.Core.DOM.Engine;
using BookHeaven.Core.DOM.Services.Abstractions;
using BookHeaven.Core.DOM.Text.Measurers;
using BookHeaven.EbookManager;
using BookHeaven.EbookManager.Entities;
using Mediator;
using Microsoft.Extensions.Options;

namespace BookHeaven.Core.DOM.Services;

/// <summary>
/// High-level entry point for page-map calculation. Uses the MiniLayoutEngine and
/// BlockPageSplitter (block-based path) to count pages from HTML + optional CSS.
/// The legacy line-based engine (DomLayoutEngine + PageSplitter) remains in the
/// codebase untouched while the block path is being validated.
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
        var chapterCss = new List<string>[chapters.Count];
        var stylesheets = ebook.Content.Stylesheets;
        for (var i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            var styles = new List<string>();

            if(!string.IsNullOrEmpty(chapter.ParagraphClassName))
            {
                styles.Add($"body p.{chapter.ParagraphClassName} {{ margin-block: calc(var(--paragraph-spacing) * 1pt) !important; }}");
            }

            // Book-stylesheet order is the cascade order; a set makes the
            // membership test O(1) instead of O(stylesheets) per chapter.
            var chapterStylesheetIds = new HashSet<string>(chapter.Stylesheets);
            foreach (var stylesheet in stylesheets)
            {
                if (chapterStylesheetIds.Contains(stylesheet.Identifier))
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
        // font directory only) is used.
        var sharedMeasurer = sender != null && urlBuilder != null
            ? await TextMeasurerFactory.CreateMeasurerAsync(normalized, sender, urlBuilder, cancellationToken)
            : TextMeasurerFactory.CreateMeasurer(coreOptions.Value.DefaultFontDirectory);
        try
        {
            // A bounded channel of chapter indices feeds a fixed pool of workers:
            // at most `workerCount` engines are alive at once, and no per-chapter
            // Task is created (a SemaphoreSlim + one Task per chapter spawned a
            // task for every chapter just to queue it).
            var workerCount = Math.Clamp(Environment.ProcessorCount, 1, MaxParallelChapters);
            var queue = Channel.CreateBounded<int>(workerCount);

            // Workers must be running before the producer writes: the channel is
            // bounded, so a producer ahead of the workers would deadlock.
            var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
            {
                await foreach (var i in queue.Reader.ReadAllAsync(cancellationToken))
                {
                    // Generate layout blocks and split them into pages (block-based path).
                    using var engine = new MiniLayoutEngine(normalized, coreOptions, sharedMeasurer);
                    var blocks = await engine.GenerateLayoutBlocksAsync(chapters[i].Content, chapterCss[i], cancellationToken);
                    var map = BlockPageSplitter.SplitToPages(blocks, normalized.PageHeightPx);

                    // Indexed write: output is identical to a sequential run, no locking needed.
                    results[i] = map.Pages.Count;
                }
            }, cancellationToken)).ToArray();

            for (var i = 0; i < chapters.Count; i++)
                await queue.Writer.WriteAsync(i, cancellationToken);
            queue.Writer.Complete();

            await Task.WhenAll(workers);
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
