using BookHeaven.Core.Extensions;
using BookHeaven.Core.Localization;

namespace BookHeaven.Core.Enums;

public enum CollectionType
{
    [StringValue(nameof(Translations.SIMPLE))]
    Simple = 0,
    [StringValue(nameof(Translations.SMART))]
    Smart = 1
}