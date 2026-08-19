namespace BookHeaven.Core.Shared
{
	public class Result
	{

		protected Result(bool isSuccess, Error error)
		{
			if(isSuccess && error != Error.None || !isSuccess && error == Error.None)
			{
				throw new ArgumentException("Invalid error", nameof(error));
			}

			IsSuccess = isSuccess;
			Error = error;
		}

		public bool IsSuccess { get; }
		public bool IsFailure => !IsSuccess;

		public Error Error { get; }

		public static Result Success() => new(true, Error.None);
		public static Result Failure(Error error) => new(false, error);
	}

	public class Result<T> : Result
	{
		private Result(bool isSuccess, Error error, T value) : base(isSuccess, error)
		{
			Value = value;
		}

		public T Value { get; }

		public static implicit operator Result<T>(T value) => new(true, Error.None, value);
    
		public static implicit operator Result<T>(Error error) => new(false, error, default!);
	}
}
