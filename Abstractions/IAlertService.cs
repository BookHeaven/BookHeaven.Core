using BookHeaven.Core.Enums;

namespace BookHeaven.Core.Abstractions;

public interface IAlertService
{
    Task ShowAlertAsync(string title, string message, string cancel = "OK");
    Task<bool> ShowConfirmationAsync(string title, string message, string accept = "Yes", string cancel = "No");
    Task<string> ShowPromptAsync(string title, string message, string accept = "Yes", string cancel = "No");
    Task ShowToastAsync(string message, AlertSeverity severity = AlertSeverity.Info);
}