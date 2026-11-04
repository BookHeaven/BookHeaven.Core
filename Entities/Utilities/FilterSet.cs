namespace BookHeaven.Core.Entities.Utilities;

public sealed class FilterSet<T>
{
    public List<T> Include { get; set; } = [];
    public List<T> Exclude { get; set; } = [];
}

