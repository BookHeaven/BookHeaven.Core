using BookHeaven.Core.DOM.Services;
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
        
        public PageCalculatorOptions ToPageCalculatorOptions(int pageWidthPx, int pageHeightPx)
        {
            return new PageCalculatorOptions
            {
                PageWidthPx = pageWidthPx,
                PageHeightPx = pageHeightPx,
                FontSize = (float)settings.FontSize,
                LineHeight = (float)settings.LineHeight,
                LetterSpacing = (float)settings.LetterSpacing,
                WordSpacing = (float)settings.WordSpacing,
                ParagraphSpacing = (float)settings.ParagraphSpacing,
                TextIndent = (float)settings.TextIndent,
                HorizontalMargin = (float)settings.HorizontalMargin,
                VerticalMargin = (float)settings.VerticalMargin,
                SelectedFont = settings.SelectedFont
            };
        }
    }
}