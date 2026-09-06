using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Features.Reader.Abstractions;

public interface IReaderSettingsService : IDisposable
{
    ProfileSettings? ReaderSettings { get; }
    event Action<string?>? OnSettingChanged;
    void NotifySettingChanged(string? settingName = null);
    Task LoadSettings(Guid profileId);
    Task SaveSettings();
}