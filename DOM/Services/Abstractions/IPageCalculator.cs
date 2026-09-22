using BookHeaven.EbookManager.Entities;

namespace BookHeaven.Core.DOM.Services.Abstractions;

/// <summary>
/// Abstraction for page calculation over `Ebook` objects.
/// Implementaciones calculan el número de páginas por capítulo del `Ebook`.
/// </summary>
public interface IPageCalculator
{
    /// <summary>
    /// Calcula el número de páginas por capítulo para el <see cref="Ebook"/> proporcionado.
    /// El array devuelto tiene la misma longitud que <c>ebook.Content.Chapters</c>;
    /// cada elemento <c>result[i]</c> es el número de páginas del capítulo <c>i</c>.
    /// </summary>
    Task<int[]> CalculatePagesAsync(Ebook ebook, PageCalculatorOptions options, CancellationToken cancellationToken = default);
}
