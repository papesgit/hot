using System.Threading.Tasks;
using Avalonia.Controls;
using HlaeObsTools.Services.Settings;
using HlaeObsTools.Views;

namespace HlaeObsTools.Services.Survey;

public sealed class SurveyPromptService
{
    // Increment this for each new survey. Users who declined an earlier survey
    // will then be asked about the new one unless they opt out again.
    private const int CurrentSurveyNumber = 1;
    private const string SurveyUrl = "https://hot.papesmedia.com/survey";

    private readonly SettingsStorage _settingsStorage = new();

    public async Task ShowIfNeededAsync(Window owner)
    {
        if (_settingsStorage.Load().DismissedSurveyNumber >= CurrentSurveyNumber)
            return;

        var response = await SurveyDialog.ShowAsync(owner, SurveyUrl);
        if (response == SurveyDialogResult.DontRemind)
        {
            _settingsStorage.Update(settings =>
                settings.DismissedSurveyNumber = System.Math.Max(
                    settings.DismissedSurveyNumber,
                    CurrentSurveyNumber));
        }
    }
}
