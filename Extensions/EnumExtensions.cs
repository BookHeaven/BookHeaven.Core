using System.Reflection;
using BookHeaven.Core.Enums;
using BookHeaven.Core.Localization;

namespace BookHeaven.Core.Extensions;

[AttributeUsage(AttributeTargets.Field)]
public sealed class StringValueAttribute(string resourceKey) : Attribute
{
    public string Value { get; } = Translations.ResourceManager.GetString(resourceKey) ?? resourceKey;
}
public static class EnumExtensions
{
    public static string StringValue<T>(this T value) where T : Enum
    {
        var fieldName = value.ToString();
        var field = typeof(T).GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
        return field?.GetCustomAttribute<StringValueAttribute>()?.Value ?? fieldName;
    }
    
    public static string GetExtension(this EbookFormat format) => format switch
    {
        EbookFormat.Epub => ".epub",
        EbookFormat.Pdf => ".pdf",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };
    
    public static EbookFormat GetFormatByExtension(string extension) => extension.ToLower() switch
    {
        ".epub" => EbookFormat.Epub,
        ".pdf" => EbookFormat.Pdf,
        _ => throw new ArgumentOutOfRangeException(nameof(extension), extension, null)
    };
    
    public static string GetMimeType(this EbookFormat format) => format switch
    {
        EbookFormat.Epub => "application/epub+zip",
        EbookFormat.Pdf => "application/pdf",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };
}
