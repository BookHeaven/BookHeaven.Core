using BookHeaven.Core.Shared;
using Mediator;

namespace BookHeaven.Core.Abstractions.Messaging;

public interface IQuery<TResponse> : IRequest<Result<TResponse>>
{
}