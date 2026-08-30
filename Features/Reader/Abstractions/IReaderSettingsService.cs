using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Features.Reader.Abstractions;

public interface IReaderSettingsService : IDisposable
{
    ProfileSettings? ReaderSettings { get; }
    event Action? OnSettingsChanged;
    void NotifySettingsChanged();
    Task LoadSettings(Guid profileId);
    Task SaveSettings();
}