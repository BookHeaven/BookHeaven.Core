namespace BookHeaven.Core.Helpers;

public static class Helpers
{
	/// <summary>
	/// Prettier null coalescing operator when there's no else statement.
	/// </summary>
	public static T? Then<T>(this bool value, T result)
	{
		return value ? result : default;
	}
}