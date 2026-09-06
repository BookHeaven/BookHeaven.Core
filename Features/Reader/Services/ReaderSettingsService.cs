using BookHeaven.Core.Entities;
using BookHeaven.Core.Features.ProfileSettingss;
using BookHeaven.Core.Features.Reader.Abstractions;
using Mediator;

namespace BookHeaven.Core.Features.Reader.Services;

public class ReaderSettingsService(ISender sender) : IReaderSettingsService
{
    public ProfileSettings? ReaderSettings { get; private set; }
    public event Action<string?>? OnSettingChanged;

    public async Task LoadSettings(Guid profileId)
    {
        var getSettings = await sender.Send(new GetProfileSettings.Query(profileId));
        ReaderSettings = getSettings.IsSuccess ? getSettings.Value : new() {ProfileId = profileId};
        if (ReaderSettings.ProfileSettingsId == Guid.Empty)
        {
            await sender.Send(new AddProfileSettings.Command(ReaderSettings));
        }
    }

    public async Task SaveSettings()
    {
        if(ReaderSettings is null) return;
        await sender.Send(new UpdateProfileSettings.Command(ReaderSettings));
    }
    
    public void NotifySettingChanged(string? settingName = null)
    {
        OnSettingChanged?.Invoke(settingName);
        _ = SaveSettings();
    }
    
    public void Dispose()
    {
        ReaderSettings = null;
        GC.SuppressFinalize(this);
    }
}