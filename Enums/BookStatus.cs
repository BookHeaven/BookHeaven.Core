using BookHeaven.Core.Extensions;
using BookHeaven.Core.Localization;

namespace BookHeaven.Core.Enums;

public enum BookStatus
{
    [StringValue(nameof(Translations.ALL_M))]
    All,
    [StringValue(nameof(Translations.NEW))]
    New,
    [StringValue(nameof(Translations.READING))]
    Reading,
    [StringValue(nameof(Translations.FINISHED))]
    Finished
}