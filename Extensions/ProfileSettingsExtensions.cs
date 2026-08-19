using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Extensions;

public static class ProfileSettingsExtensions
{
    extension(ProfileSettings settings)
    {
        public void UpdateFrom(ProfileSettings updatedSettings)
        {
            settings.FontSize = updatedSettings.FontSize;
            settings.LineHeight = updatedSettings.LineHeight;
            settings.LetterSpacing = updatedSettings.LetterSpacing;
            settings.WordSpacing = updatedSettings.WordSpacing;
            settings.ParagraphSpacing = updatedSettings.ParagraphSpacing;
            settings.TextIndent = updatedSettings.TextIndent;
            settings.HorizontalMargin = updatedSettings.HorizontalMargin;
            settings.VerticalMargin = updatedSettings.VerticalMargin;
            settings.PageGap = updatedSettings.PageGap;
            settings.SelectedLayout = updatedSettings.SelectedLayout;
            settings.SelectedFont = updatedSettings.SelectedFont;
        }
    }
}