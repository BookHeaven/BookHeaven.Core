using BookHeaven.Core.Abstractions.Events;

namespace BookHeaven.Core.Events;

public record BookAdded(Guid BookId) : IEvent;
public record BookUpdated(Guid BookId) : IEvent;
public record BookDeleted(Guid BookId) : IEvent;

public record CollectionCreated(Guid CollectionId) : IEvent;
public record CollectionUpdated(Guid CollectionId) : IEvent;
public record CollectionsUpdated : IEvent;
public record CollectionsPositionsUpdated(List<(Guid, int)> UpdatedPositions) : IEvent;
public record CollectionDeleted(Guid CollectionId) : IEvent;
