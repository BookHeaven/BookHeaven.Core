namespace BookHeaven.Core.Shared;

public sealed record Error(string Description)
{
	public static readonly Error None = new Error(string.Empty);

	public static implicit operator Result(Error error) => Result.Failure(error);
}
