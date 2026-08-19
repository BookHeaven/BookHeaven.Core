using BookHeaven.Core.Abstractions.Events;

namespace BookHeaven.Core.Services;

public class GlobalEventsService
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();
    
    public void Subscribe<T>(Func<T, Task> handler) where T : IEvent
    {
        if (!_handlers.ContainsKey(typeof(T)))
            _handlers[typeof(T)] = [];
        _handlers[typeof(T)].Add(handler);
    }
    
    public void Unsubscribe<T>(Func<T, Task> handler) where T : IEvent
    {
        if (_handlers.TryGetValue(typeof(T), out var handlers))
        {
            handlers.Remove(handler);
        }
    }

    public async Task Publish<T>(T eventData) where T : IEvent
    {
        if (_handlers.TryGetValue(typeof(T), out var handlers))
        {
            foreach (var handler in handlers.OfType<Func<T, Task>>().ToList())
            {
                await handler(eventData);
            }
        }
    }
}