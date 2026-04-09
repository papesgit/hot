using System;

namespace HlaeObsTools.ViewModels;

public sealed class StartupProgressViewModel : ViewModelBase
{
    private string _statusText = "Starting application...";
    private string _detailText = "Preparing startup sequence.";
    private double _progressValue;
    private double _progressMaximum = 17;

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string DetailText
    {
        get => _detailText;
        set => SetProperty(ref _detailText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set
        {
            if (SetProperty(ref _progressValue, value))
            {
                OnPropertyChanged(nameof(StepText));
                OnPropertyChanged(nameof(ProgressFraction));
                OnPropertyChanged(nameof(ProgressPercentText));
            }
        }
    }

    public double ProgressMaximum
    {
        get => _progressMaximum;
        set
        {
            if (SetProperty(ref _progressMaximum, value))
            {
                OnPropertyChanged(nameof(StepText));
                OnPropertyChanged(nameof(ProgressFraction));
                OnPropertyChanged(nameof(ProgressPercentText));
            }
        }
    }

    public double ProgressFraction => _progressMaximum <= 0 ? 0 : Math.Clamp(_progressValue / _progressMaximum, 0, 1);

    public string StepText
    {
        get
        {
            var currentStep = Math.Max(0, (int)Math.Ceiling(_progressValue));
            var totalSteps = Math.Max(1, (int)Math.Ceiling(_progressMaximum));
            return $"Step {currentStep} of {totalSteps}";
        }
    }

    public string ProgressPercentText => $"{Math.Round(ProgressFraction * 100):0}%";
}
