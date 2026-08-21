using System.Text.Json.Serialization;

namespace BookHeaven.Core.Enums;

public enum EbookFormat
{
    [JsonIgnore]
    None = 0,
    Epub = 1,
    Pdf = 2
}