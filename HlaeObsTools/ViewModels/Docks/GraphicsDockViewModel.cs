using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.Graphics;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels.Docks;

public sealed class GraphicsDockViewModel : Tool, IDisposable
{
    private enum GraphicsSetupSelection
    {
        None,
        Atlas,
        Region,
        Instance
    }

    private sealed record GraphicsClipboardItem(string Type, object Data);

    private readonly GraphicsService _graphicsService;

    private bool _isSetupView;
    private GraphicsAtlasViewModel? _selectedAtlas;
    private GraphicsAtlasViewModel? _selectedAtlasNavigation;
    private GraphicsRegionViewModel? _selectedRegion;
    private GraphicsInstanceViewModel? _selectedInstance;
    private GraphicsAtlasViewModel? _selectedInstanceAtlas;
    private GraphicsRegionViewModel? _selectedInstanceRegion;
    private GraphicsInstanceSourceOption? _selectedInstanceSource;
    private string? _selectedInstanceImageFile;
    private AttachSlotOption? _selectedInstanceAttachSlot;
    private AttachAttachmentOption? _selectedInstanceAttachment;
    private AttachAttachmentOption? _selectedInstanceBone;
    private string _selectedProfileName = GraphicsProfileStorage.EmptyProfileName;
    private GraphicsProfileListItem? _selectedProfile;
    private string _toastMessage = string.Empty;
    private bool _isToastVisible;
    private CancellationTokenSource? _toastCts;
    private bool _disposed;
    private bool _suppressApply;
    private bool _suppressInstanceSelectionApply;
    private bool _refreshingProfiles;
    private GraphicsSetupSelection _setupSelection;
    public event EventHandler<string>? ProfileRemoved;

    public ObservableCollection<GraphicsAtlasViewModel> Atlases { get; } = new();
    public ObservableCollection<GraphicsInstanceViewModel> Instances { get; } = new();
    public ObservableCollection<GraphicsRegionViewModel> SelectedRegions { get; } = new();
    public ObservableCollection<GraphicsInstanceViewModel> SelectedInstances { get; } = new();
    public ObservableCollection<GraphicsInstanceSourceOption> InstanceSourceOptions { get; } = new();
    public ObservableCollection<string> AvailableImages { get; } = new();
    public ObservableCollection<AttachSlotOption> AttachSlotOptions { get; } = new();
    public ObservableCollection<AttachAttachmentOption> AttachAttachmentOptions { get; } = new();
    public ObservableCollection<AttachAttachmentOption> AttachBoneOptions { get; } = new();
    public ObservableCollection<GraphicsProfileListItem> Profiles { get; } = new();

    public GraphicsDockViewModel(GraphicsService graphicsService)
    {
        _graphicsService = graphicsService;

        Title = "Graphics";
        CanClose = true;
        CanFloat = true;
        CanPin = true;

        InstanceSourceOptions.Add(new GraphicsInstanceSourceOption("Atlas", GraphicsInstanceSourceType.Atlas));
        InstanceSourceOptions.Add(new GraphicsInstanceSourceOption("Image", GraphicsInstanceSourceType.Image));
        AttachSlotOptions.Add(new AttachSlotOption("None", -1));
        for (var i = 0; i < 9; i++)
        {
            AttachSlotOptions.Add(new AttachSlotOption($"Slot {i + 1}", i));
        }
        AttachSlotOptions.Add(new AttachSlotOption("Slot 0", 9));
        AttachAttachmentOptions.Add(new AttachAttachmentOption("None", string.Empty));
        foreach (var attachment in AttachPresetViewModel.DefaultAttachmentOptionsList)
        {
            AttachAttachmentOptions.Add(new AttachAttachmentOption(attachment, attachment));
        }
        AttachBoneOptions.Add(new AttachAttachmentOption("None", string.Empty));
        foreach (var bone in AttachPresetViewModel.DefaultBoneOptionsList)
        {
            AttachBoneOptions.Add(new AttachAttachmentOption(bone, bone.Replace(" (CT)", string.Empty)));
        }
        RefreshProfiles();
        SelectedProfileName = _graphicsService.CurrentProfileName;
        RefreshFromProfile();

        _graphicsService.ProfileChanged += OnProfileChanged;
        _graphicsService.DirtyStateChanged += OnDirtyStateChanged;
        _graphicsService.InstancesVisibilityChanged += OnInstancesVisibilityChanged;

        ShowSetupCommand = new Relay(() => IsSetupView = true);
        ShowLiveCommand = new Relay(() => IsSetupView = false);
        ApplyCommand = new Relay(async () => await ApplyAsync());
        SaveProfileCommand = new Relay(SaveCurrentProfile);
        ReloadAllCommand = new Relay(async () => await ReloadAllAsync());
        ShowAllCommand = new Relay(() =>
        {
            _ = SetAllInstancesVisibleAsync(true);
        });
        HideAllCommand = new Relay(() =>
        {
            _ = SetAllInstancesVisibleAsync(false);
        });
        ReloadAtlasCommand = new Relay<GraphicsAtlasViewModel>(atlas => _ = ReloadAtlasAsync(atlas));
        AnimInAtlasCommand = new Relay<GraphicsAtlasViewModel>(atlas => _ = TriggerAtlasAsync(atlas, "animIn"));
        AnimOutAtlasCommand = new Relay<GraphicsAtlasViewModel>(atlas => _ = TriggerAtlasAsync(atlas, "animOut"));
        AnimInInstanceCommand = new Relay<GraphicsInstanceViewModel>(instance => _ = TriggerInstanceAsync(instance, "animIn"));
        AnimOutInstanceCommand = new Relay<GraphicsInstanceViewModel>(instance => _ = TriggerInstanceAsync(instance, "animOut"));
        RefreshImagesCommand = new Relay(async () => await RefreshAvailableImagesAsync());
        GetCurrentCameraPositionCommand = new Relay(async () => await GetCurrentCameraTransformAsync(copyPosition: true, copyRotation: false));
        GetCurrentCameraRotationCommand = new Relay(async () => await GetCurrentCameraTransformAsync(copyPosition: false, copyRotation: true));

        AddAtlasCommand = new Relay(AddAtlas);
        RemoveAtlasCommand = new Relay(RemoveSelectedAtlas);
        AddRegionCommand = new Relay(AddRegion);
        RemoveRegionCommand = new Relay(RemoveSelectedRegion);
        AddInstanceCommand = new Relay(AddInstance);
        RemoveInstanceCommand = new Relay(RemoveSelectedInstance);
        DuplicateSelectedCommand = new Relay(DuplicateSelected);
        DeleteSelectedCommand = new Relay(DeleteSelected);

        _ = RefreshAvailableImagesAsync();
    }

    public bool IsSetupView
    {
        get => _isSetupView;
        set => SetProperty(ref _isSetupView, value);
    }

    public GraphicsAtlasViewModel? SelectedAtlas
    {
        get => _selectedAtlas;
        set
        {
            if (!SetProperty(ref _selectedAtlas, value))
                return;
            SetSetupSelection(GraphicsSetupSelection.Atlas);
        }
    }

    public GraphicsRegionViewModel? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (!SetProperty(ref _selectedRegion, value))
                return;
            if (value != null)
                SetSetupSelection(GraphicsSetupSelection.Region);
        }
    }

    public GraphicsInstanceViewModel? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!SetProperty(ref _selectedInstance, value))
                return;
            if (value != null)
                SetSetupSelection(GraphicsSetupSelection.Instance);
            _suppressInstanceSelectionApply = true;
            try
            {
                SelectedInstanceSource = ResolveInstanceSource(_selectedInstance?.SourceType ?? GraphicsInstanceSourceType.Atlas);
                SelectedInstanceAtlas = ResolveAtlasByName(_selectedInstance?.Atlas);
                _selectedInstanceImageFile = ResolveImageFile(_selectedInstance?.ImageFile);
                OnPropertyChanged(nameof(SelectedInstanceImageFile));
                SelectedInstanceAttachSlot = ResolveAttachSlot(_selectedInstance?.AttachSlot ?? -1);
                SelectedInstanceAttachment = ResolveAttachmentName(_selectedInstance?.AttachAttachmentName);
                SelectedInstanceBone = ResolveBoneName(_selectedInstance?.AttachBoneName);
                SelectedInstanceRegion = ResolveRegionById(SelectedInstanceAtlas, _selectedInstance?.Region);
            }
            finally
            {
                _suppressInstanceSelectionApply = false;
            }
        }
    }

    public GraphicsInstanceSourceOption? SelectedInstanceSource
    {
        get => _selectedInstanceSource;
        set
        {
            if (!SetProperty(ref _selectedInstanceSource, value))
                return;
            if (!_suppressInstanceSelectionApply && SelectedInstance != null && value != null)
            {
                foreach (var instance in GetSelectedInstances())
                    instance.SourceType = value.Value;
                foreach (var atlas in Atlases)
                {
                    UpdateAtlasInstancesVisibilityState(atlas);
                }
            }
            OnPropertyChanged(nameof(IsAtlasSourceSelected));
            OnPropertyChanged(nameof(IsImageSourceSelected));
        }
    }

    public GraphicsAtlasViewModel? SelectedInstanceAtlas
    {
        get => _selectedInstanceAtlas;
        set
        {
            // ComboBox can transiently clear its selection while its ItemsSource is rebuilt.
            // Atlas-source instances do not have a meaningful empty atlas selection, so do not
            // let that UI transition erase the saved atlas and region values.
            if (value == null && !_suppressInstanceSelectionApply && SelectedInstance != null)
                return;
            if (!SetProperty(ref _selectedInstanceAtlas, value))
                return;
            if (!_suppressInstanceSelectionApply && SelectedInstance != null)
            {
                foreach (var instance in GetSelectedInstances())
                    instance.Atlas = value?.Name ?? string.Empty;
                foreach (var atlas in Atlases)
                {
                    UpdateAtlasInstancesVisibilityState(atlas);
                }
            }
            SelectedInstanceRegion = ResolveRegionById(_selectedInstanceAtlas, _selectedInstance?.Region);
        }
    }

    public string? SelectedInstanceImageFile
    {
        get => _selectedInstanceImageFile;
        set
        {
            if (!SetProperty(ref _selectedInstanceImageFile, value))
                return;
            if (!_suppressInstanceSelectionApply && SelectedInstance != null)
            {
                foreach (var instance in GetSelectedInstances())
                    instance.ImageFile = value ?? string.Empty;
            }
        }
    }

    public GraphicsRegionViewModel? SelectedInstanceRegion
    {
        get => _selectedInstanceRegion;
        set
        {
            // See SelectedInstanceAtlas: ignore transient null ComboBox selections.
            if (value == null && !_suppressInstanceSelectionApply && SelectedInstance != null)
                return;
            if (!SetProperty(ref _selectedInstanceRegion, value))
                return;
            if (!_suppressInstanceSelectionApply && SelectedInstance != null)
            {
                foreach (var instance in GetSelectedInstances())
                    instance.Region = value?.Id ?? string.Empty;
            }
        }
    }

    public AttachSlotOption? SelectedInstanceAttachSlot
    {
        get => _selectedInstanceAttachSlot;
        set
        {
            if (!SetProperty(ref _selectedInstanceAttachSlot, value))
                return;
            if (!_suppressInstanceSelectionApply && SelectedInstance != null && value != null)
            {
                foreach (var instance in GetSelectedInstances())
                    instance.AttachSlot = value.Value;
            }
        }
    }

    public AttachAttachmentOption? SelectedInstanceAttachment
    {
        get => _selectedInstanceAttachment;
        set
        {
            if (!SetProperty(ref _selectedInstanceAttachment, value))
                return;
            if (!_suppressInstanceSelectionApply && SelectedInstance != null && value != null)
            {
                foreach (var instance in GetSelectedInstances())
                {
                    instance.AttachAttachmentName = value.Value;
                    if (!string.IsNullOrEmpty(instance.AttachAttachmentName))
                        instance.AttachBoneName = string.Empty;
                }
                if (!string.IsNullOrEmpty(value.Value))
                    SetProperty(ref _selectedInstanceBone, AttachBoneOptions.FirstOrDefault(), nameof(SelectedInstanceBone));
            }
        }
    }

    public GraphicsAtlasViewModel? SelectedAtlasNavigation
    {
        get => _selectedAtlasNavigation;
        set
        {
            if (!SetProperty(ref _selectedAtlasNavigation, value) || value == null)
                return;
            SelectedAtlas = value;
            ClearSelection(SelectedRegions);
            ClearSelection(SelectedInstances);
            UpdateAtlasScope();
        }
    }

    public AttachAttachmentOption? SelectedInstanceBone
    {
        get => _selectedInstanceBone;
        set
        {
            if (!SetProperty(ref _selectedInstanceBone, value)) return;
            if (!_suppressInstanceSelectionApply && SelectedInstance != null && value != null)
            {
                foreach (var instance in GetSelectedInstances())
                {
                    instance.AttachBoneName = value.Value;
                    if (!string.IsNullOrEmpty(instance.AttachBoneName))
                        instance.AttachAttachmentName = string.Empty;
                }
                if (!string.IsNullOrEmpty(value.Value))
                    SetProperty(ref _selectedInstanceAttachment, AttachAttachmentOptions.FirstOrDefault(), nameof(SelectedInstanceAttachment));
            }
        }
    }

    public string SelectedProfileName
    {
        get => _selectedProfileName;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                // Clearing ItemsSource makes ComboBox transiently push a null selection.
                // Keep the active profile while RefreshProfiles rebuilds the list.
                if (_refreshingProfiles)
                    return;
                return;
            }
            if (!SetProperty(ref _selectedProfileName, value))
                return;
            OnPropertyChanged(nameof(CanRemoveSelectedProfile));
            OnPropertyChanged(nameof(IsEmptyProfile));
            OnPropertyChanged(nameof(CanManageSelectedProfile));
        }
    }

    public string ToastMessage
    {
        get => _toastMessage;
        private set => SetProperty(ref _toastMessage, value);
    }

    public bool IsToastVisible
    {
        get => _isToastVisible;
        private set => SetProperty(ref _isToastVisible, value);
    }

    public bool CanRemoveSelectedProfile => !GraphicsProfileStorage.IsReservedProfileName(SelectedProfileName);
    public bool IsEmptyProfile => GraphicsProfileStorage.IsReservedProfileName(SelectedProfileName);
    public bool CanManageSelectedProfile => !IsEmptyProfile;

    public bool IsAtlasSourceSelected => SelectedInstanceSource?.Value == GraphicsInstanceSourceType.Atlas;

    public bool IsImageSourceSelected => SelectedInstanceSource?.Value == GraphicsInstanceSourceType.Image;

    public bool HasSelection => _setupSelection != GraphicsSetupSelection.None;
    public bool HasSelectedAtlas => _setupSelection == GraphicsSetupSelection.Atlas && SelectedAtlas != null;
    public bool HasSelectedRegion => _setupSelection == GraphicsSetupSelection.Region && SelectedRegion != null;
    public bool HasSelectedInstance => _setupSelection == GraphicsSetupSelection.Instance && SelectedInstance != null;
    public bool HasMultipleRegions => SelectedRegions.Count > 1;
    public bool HasMultipleInstances => SelectedInstances.Count > 1;
    public string SelectedItemTitle => _setupSelection switch
    {
        GraphicsSetupSelection.Atlas => SelectedAtlas?.Name ?? "Atlas",
        GraphicsSetupSelection.Region => HasMultipleRegions ? $"{SelectedRegions.Count} regions selected" : SelectedRegion?.Id ?? "Region",
        GraphicsSetupSelection.Instance => HasMultipleInstances ? $"{SelectedInstances.Count} instances selected" : SelectedInstance?.Name ?? "Instance",
        _ => "Inspector"
    };

    public ICommand ShowSetupCommand { get; }
    public ICommand ShowLiveCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand ReloadAllCommand { get; }
    public ICommand ShowAllCommand { get; }
    public ICommand HideAllCommand { get; }
    public ICommand ReloadAtlasCommand { get; }
    public ICommand AnimInAtlasCommand { get; }
    public ICommand AnimOutAtlasCommand { get; }
    public ICommand AnimInInstanceCommand { get; }
    public ICommand AnimOutInstanceCommand { get; }
    public ICommand RefreshImagesCommand { get; }
    public ICommand GetCurrentCameraPositionCommand { get; }
    public ICommand GetCurrentCameraRotationCommand { get; }
    public ICommand DuplicateSelectedCommand { get; }
    public ICommand DeleteSelectedCommand { get; }

    public string RegionIdValue { get => GetMixedRegion(region => region.Id); set => SetRegions(region => region.Id = value); }
    public string RegionU0Value { get => GetMixedRegion(region => region.U0); set => SetRegionsDouble(value, (region, number) => region.U0 = number); }
    public string RegionV0Value { get => GetMixedRegion(region => region.V0); set => SetRegionsDouble(value, (region, number) => region.V0 = number); }
    public string RegionU1Value { get => GetMixedRegion(region => region.U1); set => SetRegionsDouble(value, (region, number) => region.U1 = number); }
    public string RegionV1Value { get => GetMixedRegion(region => region.V1); set => SetRegionsDouble(value, (region, number) => region.V1 = number); }
    public string RegionDefaultWidthValue { get => GetMixedRegion(region => region.DefaultWidth); set => SetRegionsDouble(value, (region, number) => region.DefaultWidth = number); }
    public string RegionDefaultHeightValue { get => GetMixedRegion(region => region.DefaultHeight); set => SetRegionsDouble(value, (region, number) => region.DefaultHeight = number); }
    public string InstanceNameValue { get => GetMixedInstance(instance => instance.Name); set => SetInstances(instance => instance.Name = value); }
    public string InstancePosXValue { get => GetMixedInstance(instance => instance.PosX); set => SetInstancesDouble(value, (instance, number) => instance.PosX = number); }
    public string InstancePosYValue { get => GetMixedInstance(instance => instance.PosY); set => SetInstancesDouble(value, (instance, number) => instance.PosY = number); }
    public string InstancePosZValue { get => GetMixedInstance(instance => instance.PosZ); set => SetInstancesDouble(value, (instance, number) => instance.PosZ = number); }
    public string InstancePitchValue { get => GetMixedInstance(instance => instance.Pitch); set => SetInstancesDouble(value, (instance, number) => instance.Pitch = number); }
    public string InstanceYawValue { get => GetMixedInstance(instance => instance.Yaw); set => SetInstancesDouble(value, (instance, number) => instance.Yaw = number); }
    public string InstanceRollValue { get => GetMixedInstance(instance => instance.Roll); set => SetInstancesDouble(value, (instance, number) => instance.Roll = number); }
    public string InstanceScaleXValue { get => GetMixedInstance(instance => instance.ScaleX); set => SetInstancesDouble(value, (instance, number) => instance.ScaleX = number); }
    public string InstanceScaleYValue { get => GetMixedInstance(instance => instance.ScaleY); set => SetInstancesDouble(value, (instance, number) => instance.ScaleY = number); }
    public bool? InstanceVisibleValue { get => GetMixedInstanceBool(instance => instance.Visible); set => SetInstancesBool(value, instance => instance.Visible = value.GetValueOrDefault()); }
    public bool? InstanceDepthTestValue { get => GetMixedInstanceBool(instance => instance.DepthTest); set => SetInstancesBool(value, instance => instance.DepthTest = value.GetValueOrDefault()); }
    public bool? InstanceDepthWriteValue { get => GetMixedInstanceBool(instance => instance.DepthWrite); set => SetInstancesBool(value, instance => instance.DepthWrite = value.GetValueOrDefault()); }
    public bool? InstanceAttachUseYawValue { get => GetMixedInstanceBool(instance => instance.AttachUseYaw); set => SetInstancesBool(value, instance => instance.AttachUseYaw = value.GetValueOrDefault()); }
    public bool? InstanceAttachUsePitchValue { get => GetMixedInstanceBool(instance => instance.AttachUsePitch); set => SetInstancesBool(value, instance => instance.AttachUsePitch = value.GetValueOrDefault()); }
    public bool? InstanceAttachUseRollValue { get => GetMixedInstanceBool(instance => instance.AttachUseRoll); set => SetInstancesBool(value, instance => instance.AttachUseRoll = value.GetValueOrDefault()); }
    public ICommand AddAtlasCommand { get; }
    public ICommand RemoveAtlasCommand { get; }
    public ICommand AddRegionCommand { get; }
    public ICommand RemoveRegionCommand { get; }
    public ICommand AddInstanceCommand { get; }
    public ICommand RemoveInstanceCommand { get; }

    private async Task ApplyAsync()
    {
        var response = await _graphicsService.ApplyProfileAsync();
        if (response.Result == GraphicsApplyResult.ProducerAtlasCreateNoResponse)
            return;

        if (response.Result == GraphicsApplyResult.Applied)
            RestoreLiveVisibilityToProfileDefaults();

        ShowToast(response.Result switch
        {
            GraphicsApplyResult.Applied => "Applied",
            GraphicsApplyResult.HlaeDisconnected => "HLAE not connected",
            GraphicsApplyResult.ProducerDisconnected => "Graphics producer not connected",
            GraphicsApplyResult.ProducerAtlasCreateFailed => BuildAtlasCreateFailedToast(response),
            _ => "Apply failed"
        });
    }

    private static string BuildAtlasCreateFailedToast(GraphicsApplyResponse response)
    {
        return response.ErrorCode switch
        {
            "invalidHtmlPath" when !string.IsNullOrWhiteSpace(response.Error) => $"Atlas HTML path invalid: {response.Error}",
            "invalidHtmlPath" => "Atlas HTML path is invalid or unavailable to the graphics producer",
            "invalidAtlasSize" => "Atlas size is invalid",
            "atlasNameRequired" => "Atlas name is required",
            _ when !string.IsNullOrWhiteSpace(response.Error) => $"Atlas create failed: {response.Error}",
            _ => "Apply finished, but an atlas failed to create"
        };
    }

    private async Task ReloadAllAsync()
    {
        var failed = false;
        var noResponse = false;
        foreach (var atlas in Atlases)
        {
            var result = await _graphicsService.ReloadAtlasAsync(atlas.Name);
            if (result == ProducerCommandResult.Failed)
            {
                failed = true;
            }
            else if (result == ProducerCommandResult.NoResponse)
            {
                noResponse = true;
            }
        }

        if (failed)
            ShowToast("Reload failed");
        else if (!noResponse)
            ShowToast("Reloaded");
    }

    public void AddAtlas(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        name = MakeUnique(name.Trim(), n => _graphicsService.Profile.Atlases.Any(a => a.Name == n));
        var atlas = new GraphicsAtlas
        {
            Name = name,
            Width = 1024,
            Height = 512,
            Format = GraphicsAtlasFormat.Bgra8,
            AlphaMode = GraphicsAlphaMode.Premultiplied,
            KeyedMutex = true,
            HtmlPath = string.Empty,
            Enabled = true
        };
        _graphicsService.Profile.Atlases.Add(atlas);
        var vm = CreateAtlasViewModel(atlas);
        Atlases.Add(vm);
        SelectedAtlas = vm;
        NotifyProfileEdited();
    }

    public GraphicsProfileListItem? SelectedProfile
    {
        get => _selectedProfile;
        set => SetProperty(ref _selectedProfile, value);
    }

    public bool HasUnsavedChanges => _graphicsService.HasUnsavedChanges;

    public void ActivateAtlas(GraphicsAtlasViewModel atlas)
    {
        SelectedAtlasNavigation = atlas;
        if (ReferenceEquals(SelectedAtlas, atlas))
            SetSetupSelection(GraphicsSetupSelection.Atlas);
        else
            SelectedAtlas = atlas;
    }

    public void ActivateRegion(GraphicsRegionViewModel region)
    {
        if (ReferenceEquals(SelectedRegion, region))
            SetSetupSelection(GraphicsSetupSelection.Region);
        else
            SelectedRegion = region;
    }

    public void SetSelectedRegions(IEnumerable<GraphicsRegionViewModel> regions)
    {
        ReplaceSelection(SelectedRegions, regions);
        if (SelectedRegions.Count == 0)
        {
            SetSetupSelection(GraphicsSetupSelection.None);
            UpdateAtlasScope();
            NotifyRegionInspectorValues();
            return;
        }

        ClearSelection(SelectedInstances);
        _selectedAtlasNavigation = null;
        OnPropertyChanged(nameof(SelectedAtlasNavigation));
        SelectedRegion = SelectedRegions[^1];
        UpdateAtlasScope();
        NotifyRegionInspectorValues();
    }

    public void SetSelectedInstances(IEnumerable<GraphicsInstanceViewModel> instances)
    {
        ReplaceSelection(SelectedInstances, instances);
        if (SelectedInstances.Count == 0)
        {
            SetSetupSelection(GraphicsSetupSelection.None);
            NotifyInstanceInspectorValues();
            return;
        }

        ClearSelection(SelectedRegions);
        _selectedAtlasNavigation = null;
        OnPropertyChanged(nameof(SelectedAtlasNavigation));
        SelectedInstance = SelectedInstances[^1];
        RefreshInstanceDropdownDisplay();
        UpdateAtlasScope();
        NotifyInstanceInspectorValues();
    }

    public void ActivateInstance(GraphicsInstanceViewModel instance)
    {
        if (ReferenceEquals(SelectedInstance, instance))
            SetSetupSelection(GraphicsSetupSelection.Instance);
        else
            SelectedInstance = instance;
    }

    private static void ReplaceSelection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        var values = source.ToList();
        if (target.SequenceEqual(values))
            return;
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }

    private static void ClearSelection<T>(ObservableCollection<T> target)
    {
        if (target.Count > 0)
            target.Clear();
    }

    private IEnumerable<GraphicsInstanceViewModel> GetSelectedInstances()
    {
        return SelectedInstances.Count > 0
            ? SelectedInstances
            : SelectedInstance == null ? [] : [SelectedInstance];
    }

    private void RefreshInstanceDropdownDisplay()
    {
        if (SelectedInstance == null)
            return;

        _suppressInstanceSelectionApply = true;
        try
        {
            _selectedInstanceSource = ResolveInstanceSource(SelectedInstance.SourceType);
            _selectedInstanceAtlas = ResolveAtlasByName(SelectedInstance.Atlas);
            _selectedInstanceImageFile = ResolveImageFile(SelectedInstance.ImageFile);
            _selectedInstanceAttachSlot = ResolveAttachSlot(SelectedInstance.AttachSlot);
            _selectedInstanceAttachment = ResolveAttachmentName(SelectedInstance.AttachAttachmentName);
            _selectedInstanceBone = ResolveBoneName(SelectedInstance.AttachBoneName);
            _selectedInstanceRegion = ResolveRegionById(_selectedInstanceAtlas, SelectedInstance.Region);
        }
        finally
        {
            _suppressInstanceSelectionApply = false;
        }

        OnPropertyChanged(nameof(SelectedInstanceSource));
        OnPropertyChanged(nameof(SelectedInstanceAtlas));
        OnPropertyChanged(nameof(SelectedInstanceImageFile));
        OnPropertyChanged(nameof(SelectedInstanceAttachSlot));
        OnPropertyChanged(nameof(SelectedInstanceAttachment));
        OnPropertyChanged(nameof(SelectedInstanceBone));
        OnPropertyChanged(nameof(SelectedInstanceRegion));
        OnPropertyChanged(nameof(IsAtlasSourceSelected));
        OnPropertyChanged(nameof(IsImageSourceSelected));
    }

    private void UpdateAtlasScope()
    {
        foreach (var atlas in Atlases)
            atlas.IsRegionScope = SelectedRegions.Count > 0 && ReferenceEquals(atlas, SelectedAtlas);
    }

    private string GetMixedRegion<T>(Func<GraphicsRegionViewModel, T> selector)
    {
        var regions = SelectedRegions.Count > 0 ? SelectedRegions : SelectedRegion == null ? [] : [SelectedRegion];
        if (regions.Count == 0)
            return string.Empty;
        var value = selector(regions[0]);
        return regions.Skip(1).All(region => EqualityComparer<T>.Default.Equals(selector(region), value))
            ? Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty
            : "-";
    }

    private void SetRegions(Action<GraphicsRegionViewModel> apply)
    {
        foreach (var region in SelectedRegions.Count > 0 ? SelectedRegions : SelectedRegion == null ? [] : [SelectedRegion])
            apply(region);
        NotifyRegionInspectorValues();
    }

    private void SetRegionsDouble(string text, Action<GraphicsRegionViewModel, double> apply)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return;
        }
        SetRegions(region => apply(region, value));
    }

    private void NotifyRegionInspectorValues()
    {
        OnPropertyChanged(nameof(RegionIdValue));
        OnPropertyChanged(nameof(RegionU0Value));
        OnPropertyChanged(nameof(RegionV0Value));
        OnPropertyChanged(nameof(RegionU1Value));
        OnPropertyChanged(nameof(RegionV1Value));
        OnPropertyChanged(nameof(RegionDefaultWidthValue));
        OnPropertyChanged(nameof(RegionDefaultHeightValue));
        OnPropertyChanged(nameof(HasMultipleRegions));
        OnPropertyChanged(nameof(SelectedItemTitle));
    }

    private string GetMixedInstance<T>(Func<GraphicsInstanceViewModel, T> selector)
    {
        var instances = SelectedInstances.Count > 0 ? SelectedInstances : SelectedInstance == null ? [] : [SelectedInstance];
        if (instances.Count == 0)
            return string.Empty;
        var value = selector(instances[0]);
        return instances.Skip(1).All(instance => EqualityComparer<T>.Default.Equals(selector(instance), value))
            ? Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty
            : "-";
    }

    private void SetInstances(Action<GraphicsInstanceViewModel> apply)
    {
        foreach (var instance in SelectedInstances.Count > 0 ? SelectedInstances : SelectedInstance == null ? [] : [SelectedInstance])
            apply(instance);
        NotifyInstanceInspectorValues();
    }

    private void SetInstancesDouble(string text, Action<GraphicsInstanceViewModel, double> apply)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return;
        }
        SetInstances(instance => apply(instance, value));
    }

    private bool? GetMixedInstanceBool(Func<GraphicsInstanceViewModel, bool> selector)
    {
        var instances = SelectedInstances.Count > 0 ? SelectedInstances : SelectedInstance == null ? [] : [SelectedInstance];
        if (instances.Count == 0)
            return null;
        var value = selector(instances[0]);
        return instances.Skip(1).All(instance => selector(instance) == value) ? value : null;
    }

    private void SetInstancesBool(bool? value, Action<GraphicsInstanceViewModel> apply)
    {
        if (value.HasValue)
            SetInstances(apply);
    }

    private void NotifyInstanceInspectorValues()
    {
        OnPropertyChanged(nameof(InstanceNameValue));
        OnPropertyChanged(nameof(InstancePosXValue));
        OnPropertyChanged(nameof(InstancePosYValue));
        OnPropertyChanged(nameof(InstancePosZValue));
        OnPropertyChanged(nameof(InstancePitchValue));
        OnPropertyChanged(nameof(InstanceYawValue));
        OnPropertyChanged(nameof(InstanceRollValue));
        OnPropertyChanged(nameof(InstanceScaleXValue));
        OnPropertyChanged(nameof(InstanceScaleYValue));
        OnPropertyChanged(nameof(InstanceVisibleValue));
        OnPropertyChanged(nameof(InstanceDepthTestValue));
        OnPropertyChanged(nameof(InstanceDepthWriteValue));
        OnPropertyChanged(nameof(InstanceAttachUseYawValue));
        OnPropertyChanged(nameof(InstanceAttachUsePitchValue));
        OnPropertyChanged(nameof(InstanceAttachUseRollValue));
        OnPropertyChanged(nameof(HasMultipleInstances));
        OnPropertyChanged(nameof(SelectedItemTitle));
    }

    public void AddRegion(string id)
    {
        if (SelectedAtlas == null || string.IsNullOrWhiteSpace(id))
            return;
        id = MakeUnique(id.Trim(), n => SelectedAtlas.Regions.Any(r => r.Id == n));
        var region = new GraphicsRegion { Id = id };
        SelectedAtlas.Model.Regions.Add(region);
        var vm = CreateRegionViewModel(region);
        SelectedAtlas.Regions.Add(vm);
        SelectedRegion = vm;
        NotifyProfileEdited();
    }

    public void AddInstance(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        name = MakeUnique(name.Trim(), n => _graphicsService.Profile.Instances.Any(i => i.Name == n));
        var instance = new GraphicsInstance
        {
            Name = name,
            SourceType = GraphicsInstanceSourceType.Atlas,
            Atlas = SelectedAtlas?.Name ?? string.Empty,
            Region = SelectedRegion?.Id ?? "full",
            ImageFile = AvailableImages.FirstOrDefault() ?? string.Empty
        };
        _graphicsService.Profile.Instances.Add(instance);
        var vm = CreateInstanceViewModel(instance);
        Instances.Add(vm);
        SelectedInstance = vm;
        NotifyProfileEdited();
    }

    private void AddAtlas()
    {
        AddAtlas("atlas");
    }

    private void AddRegion()
    {
        AddRegion("region");
    }

    private void AddInstance()
    {
        AddInstance("gfx");
    }

    public string? SerializeSelectedItem()
    {
        return _setupSelection switch
        {
            GraphicsSetupSelection.Atlas when SelectedAtlas != null => JsonSerializer.Serialize(new GraphicsClipboardItem("atlas", SelectedAtlas.Model)),
            GraphicsSetupSelection.Region when SelectedRegions.Count > 0 => JsonSerializer.Serialize(new GraphicsClipboardItem("regions", SelectedRegions.Select(region => region.Model).ToList())),
            GraphicsSetupSelection.Region when SelectedRegion != null => JsonSerializer.Serialize(new GraphicsClipboardItem("regions", new List<GraphicsRegion> { SelectedRegion.Model })),
            GraphicsSetupSelection.Instance when SelectedInstances.Count > 0 => JsonSerializer.Serialize(new GraphicsClipboardItem("instances", SelectedInstances.Select(instance => instance.Model).ToList())),
            GraphicsSetupSelection.Instance when SelectedInstance != null => JsonSerializer.Serialize(new GraphicsClipboardItem("instances", new List<GraphicsInstance> { SelectedInstance.Model })),
            _ => null
        };
    }

    public bool PasteSerializedItem(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("Type", out var typeElement)
                || !document.RootElement.TryGetProperty("Data", out var dataElement))
                return false;

            switch (typeElement.GetString())
            {
                case "atlas":
                {
                    var atlas = dataElement.Deserialize<GraphicsAtlas>();
                    if (atlas == null) return false;
                    atlas.Name = MakeUnique(atlas.Name, n => _graphicsService.Profile.Atlases.Any(a => a.Name == n));
                    _graphicsService.Profile.Atlases.Add(atlas);
                    var vm = CreateAtlasViewModel(atlas);
                    Atlases.Add(vm);
                    SelectedAtlas = vm;
                    break;
                }
                case "regions":
                {
                    if (SelectedAtlas == null) return false;
                    var regions = dataElement.Deserialize<List<GraphicsRegion>>();
                    if (regions == null || regions.Count == 0) return false;
                    var pasted = new List<GraphicsRegionViewModel>();
                    foreach (var region in regions)
                    {
                        region.Id = MakeUnique(region.Id, n => SelectedAtlas.Regions.Any(r => r.Id == n));
                        SelectedAtlas.Model.Regions.Add(region);
                        var vm = CreateRegionViewModel(region);
                        SelectedAtlas.Regions.Add(vm);
                        pasted.Add(vm);
                    }
                    SetSelectedRegions(pasted);
                    break;
                }
                case "instances":
                {
                    var instances = dataElement.Deserialize<List<GraphicsInstance>>();
                    if (instances == null || instances.Count == 0) return false;
                    var pasted = new List<GraphicsInstanceViewModel>();
                    foreach (var instance in instances)
                    {
                        instance.Name = MakeUnique(instance.Name, n => _graphicsService.Profile.Instances.Any(i => i.Name == n));
                        _graphicsService.Profile.Instances.Add(instance);
                        var vm = CreateInstanceViewModel(instance);
                        Instances.Add(vm);
                        pasted.Add(vm);
                    }
                    SetSelectedInstances(pasted);
                    break;
                }
                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        ShowToast("Pasted");
        NotifyProfileEdited();
        return true;
    }

    private void DuplicateSelected()
    {
        var serialized = SerializeSelectedItem();
        if (serialized != null && PasteSerializedItem(serialized))
            ShowToast("Duplicated");
    }

    private void DeleteSelected()
    {
        switch (_setupSelection)
        {
            case GraphicsSetupSelection.Atlas:
                RemoveSelectedAtlas();
                break;
            case GraphicsSetupSelection.Region:
                RemoveSelectedRegions();
                break;
            case GraphicsSetupSelection.Instance:
                RemoveSelectedInstances();
                break;
        }
    }

    private void RemoveSelectedAtlas()
    {
        var atlas = SelectedAtlas;
        if (atlas == null) return;
        Atlases.Remove(atlas);
        _graphicsService.Profile.Atlases.Remove(atlas.Model);
        SelectedAtlas = Atlases.FirstOrDefault();
        NotifyProfileEdited();
    }

    private void RemoveSelectedRegion()
    {
        var atlas = SelectedAtlas;
        var region = SelectedRegion;
        if (atlas == null || region == null) return;
        atlas.Regions.Remove(region);
        atlas.Model.Regions.Remove(region.Model);
        SelectedRegion = atlas.Regions.FirstOrDefault();
        NotifyProfileEdited();
    }

    private void RemoveSelectedRegions()
    {
        if (SelectedRegions.Count == 0)
        {
            RemoveSelectedRegion();
            return;
        }

        var atlas = SelectedAtlas;
        if (atlas == null) return;
        foreach (var region in SelectedRegions.ToList())
        {
            atlas.Regions.Remove(region);
            atlas.Model.Regions.Remove(region.Model);
        }
        SelectedRegions.Clear();
        SelectedRegion = atlas.Regions.FirstOrDefault();
        SetSetupSelection(GraphicsSetupSelection.None);
        NotifyProfileEdited();
    }

    private void RemoveSelectedInstance()
    {
        var instance = SelectedInstance;
        if (instance == null) return;
        Instances.Remove(instance);
        _graphicsService.Profile.Instances.Remove(instance.Model);
        SelectedInstance = Instances.FirstOrDefault();
        NotifyProfileEdited();
    }

    private void RemoveSelectedInstances()
    {
        if (SelectedInstances.Count == 0)
        {
            RemoveSelectedInstance();
            return;
        }

        foreach (var instance in SelectedInstances.ToList())
        {
            Instances.Remove(instance);
            _graphicsService.Profile.Instances.Remove(instance.Model);
        }
        SelectedInstances.Clear();
        SelectedInstance = Instances.FirstOrDefault();
        SetSetupSelection(GraphicsSetupSelection.None);
        NotifyProfileEdited();
    }

    private void SetSetupSelection(GraphicsSetupSelection selection)
    {
        if (_setupSelection == selection)
            return;
        _setupSelection = selection;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasSelectedAtlas));
        OnPropertyChanged(nameof(HasSelectedRegion));
        OnPropertyChanged(nameof(HasSelectedInstance));
        OnPropertyChanged(nameof(SelectedItemTitle));
    }

    private void RefreshFromProfile()
    {
        _suppressApply = true;
        ClearSelection(SelectedRegions);
        ClearSelection(SelectedInstances);
        _selectedAtlasNavigation = null;
        OnPropertyChanged(nameof(SelectedAtlasNavigation));
        Atlases.Clear();
        foreach (var atlas in _graphicsService.Profile.Atlases)
        {
            Atlases.Add(CreateAtlasViewModel(atlas));
        }
        Instances.Clear();
        foreach (var inst in _graphicsService.Profile.Instances)
        {
            Instances.Add(CreateInstanceViewModel(inst));
        }

        foreach (var atlas in Atlases)
        {
            UpdateAtlasInstancesVisibilityState(atlas);
        }

        SelectedAtlas = Atlases.FirstOrDefault();
        SelectedInstance = Instances.FirstOrDefault();
        SetSetupSelection(GraphicsSetupSelection.None);
        _suppressApply = false;
    }

    private static string MakeUnique(string baseName, Func<string, bool> exists)
    {
        if (!exists(baseName))
            return baseName;
        var index = 2;
        while (true)
        {
            var candidate = $"{baseName}_{index}";
            if (!exists(candidate))
                return candidate;
            index++;
        }
    }

    private GraphicsAtlasViewModel? ResolveAtlasByName(string? name)
    {
        if (HasMixedInstanceValue(instance => instance.Atlas))
            return null;
        if (string.IsNullOrWhiteSpace(name))
            return Atlases.FirstOrDefault();
        return Atlases.FirstOrDefault(atlas => atlas.Name == name) ?? Atlases.FirstOrDefault();
    }

    private GraphicsRegionViewModel? ResolveRegionById(GraphicsAtlasViewModel? atlas, string? id)
    {
        if (HasMixedInstanceValue(instance => instance.Region))
            return null;
        if (atlas == null)
            return null;
        if (string.IsNullOrWhiteSpace(id))
            return atlas.Regions.FirstOrDefault();
        return atlas.Regions.FirstOrDefault(region => region.Id == id) ?? atlas.Regions.FirstOrDefault();
    }

    private GraphicsInstanceSourceOption? ResolveInstanceSource(GraphicsInstanceSourceType sourceType)
    {
        if (HasMixedInstanceValue(instance => instance.SourceType))
            return null;
        return InstanceSourceOptions.FirstOrDefault(option => option.Value == sourceType) ?? InstanceSourceOptions.FirstOrDefault();
    }

    private string? ResolveImageFile(string? imageFile)
    {
        if (HasMixedInstanceValue(instance => instance.ImageFile))
            return null;
        if (string.IsNullOrWhiteSpace(imageFile))
            return AvailableImages.FirstOrDefault();
        return AvailableImages.FirstOrDefault(image => string.Equals(image, imageFile, StringComparison.OrdinalIgnoreCase)) ?? imageFile;
    }

    private AttachSlotOption? ResolveAttachSlot(int slot)
    {
        if (HasMixedInstanceValue(instance => instance.AttachSlot))
            return null;
        return AttachSlotOptions.FirstOrDefault(option => option.Value == slot) ?? AttachSlotOptions.FirstOrDefault();
    }

    private AttachAttachmentOption? ResolveAttachmentName(string? name)
    {
        if (HasMixedInstanceValue(instance => instance.AttachAttachmentName))
            return null;
        if (string.IsNullOrWhiteSpace(name))
            return AttachAttachmentOptions.FirstOrDefault();
        return AttachAttachmentOptions.FirstOrDefault(option => option.Value == name) ?? AttachAttachmentOptions.FirstOrDefault();
    }

    private AttachAttachmentOption? ResolveBoneName(string? name)
    {
        if (HasMixedInstanceValue(instance => instance.AttachBoneName))
            return null;
        if (string.IsNullOrWhiteSpace(name)) return AttachBoneOptions.FirstOrDefault();
        return AttachBoneOptions.FirstOrDefault(option => option.Value == name) ?? AttachBoneOptions.FirstOrDefault();
    }

    private bool HasMixedInstanceValue<T>(Func<GraphicsInstanceViewModel, T> selector)
    {
        var instances = GetSelectedInstances().ToList();
        return instances.Count > 1
            && instances.Skip(1).Any(instance => !EqualityComparer<T>.Default.Equals(selector(instance), selector(instances[0])));
    }

    public async Task RefreshAvailableImagesAsync()
    {
        var images = await _graphicsService.ListAvailableImagesAsync();
        var currentSelection = SelectedInstance?.ImageFile;

        AvailableImages.Clear();
        foreach (var image in images.OrderBy(image => image, StringComparer.OrdinalIgnoreCase))
        {
            AvailableImages.Add(image);
        }

        _selectedInstanceImageFile = ResolveImageFile(currentSelection);
        OnPropertyChanged(nameof(SelectedInstanceImageFile));
    }

    private async Task GetCurrentCameraTransformAsync(bool copyPosition, bool copyRotation)
    {
        if (SelectedInstance == null)
            return;

        var camera = await _graphicsService.GetCurrentCameraTransformAsync();
        if (camera == null)
            return;

        if (copyPosition)
        {
            SelectedInstance.PosX = camera.PosX;
            SelectedInstance.PosY = camera.PosY;
            SelectedInstance.PosZ = camera.PosZ;
        }

        if (copyRotation)
        {
            SelectedInstance.Pitch = camera.Pitch;
            SelectedInstance.Yaw = camera.Yaw;
            SelectedInstance.Roll = camera.Roll;
        }

        NotifyInstanceInspectorValues();
    }

    private async Task SetAllInstancesVisibleAsync(bool visible)
    {
        _suppressApply = true;
        foreach (var inst in Instances)
        {
            inst.LiveVisible = visible;
        }
        foreach (var atlas in Atlases)
        {
            UpdateAtlasInstancesVisibilityState(atlas);
        }
        _suppressApply = false;
        await _graphicsService.UpdateInstancesVisibilityAsync(_graphicsService.Profile.Instances, visible);
    }

    private void RestoreLiveVisibilityToProfileDefaults()
    {
        _suppressApply = true;
        try
        {
            foreach (var instance in Instances)
                instance.SetLiveVisibleInternal(instance.Visible);
            foreach (var atlas in Atlases)
                UpdateAtlasInstancesVisibilityState(atlas);
        }
        finally
        {
            _suppressApply = false;
        }
    }

    private async Task SetAtlasInstancesVisibleAsync(GraphicsAtlasViewModel atlas, bool visible)
    {
        var related = Instances
            .Where(inst => inst.SourceType == GraphicsInstanceSourceType.Atlas && inst.Atlas == atlas.Name)
            .ToList();
        _suppressApply = true;
        foreach (var inst in related)
        {
            inst.LiveVisible = visible;
        }
        _suppressApply = false;
        atlas.SetInstancesVisibleInternal(visible);
        await _graphicsService.UpdateInstancesVisibilityAsync(related.Select(r => r.Model), visible);
    }

    private async Task SetInstanceVisibleAsync(GraphicsInstanceViewModel instance, bool visible)
    {
        _suppressApply = true;
        instance.LiveVisible = visible;
        _suppressApply = false;
        await _graphicsService.UpdateInstanceVisibilityAsync(instance.Name, visible);
        if (instance.SourceType != GraphicsInstanceSourceType.Atlas)
            return;
        var atlas = Atlases.FirstOrDefault(a => a.Name == instance.Atlas);
        if (atlas != null)
        {
            UpdateAtlasInstancesVisibilityState(atlas);
        }
    }

    private void UpdateAtlasInstancesVisibilityState(GraphicsAtlasViewModel atlas)
    {
        var related = Instances
            .Where(inst => inst.SourceType == GraphicsInstanceSourceType.Atlas && inst.Atlas == atlas.Name)
            .ToList();
        if (related.Count == 0)
        {
            atlas.SetInstancesVisibleInternal(false);
            return;
        }
        var allVisible = related.All(inst => inst.LiveVisible);
        atlas.SetInstancesVisibleInternal(allVisible);
    }

    private async Task ReloadAtlasAsync(GraphicsAtlasViewModel? atlas)
    {
        if (atlas == null)
            return;
        var result = await _graphicsService.ReloadAtlasAsync(atlas.Name);
        if (result == ProducerCommandResult.Succeeded)
            ShowToast("Reloaded");
        else if (result == ProducerCommandResult.Failed)
            ShowToast("Reload failed");
    }

    private async Task TriggerAtlasAsync(GraphicsAtlasViewModel? atlas, string action)
    {
        if (atlas == null)
            return;
        await _graphicsService.TriggerAtlasInstancesAsync(atlas.Name, action);
    }

    private async Task TriggerInstanceAsync(GraphicsInstanceViewModel? instance, string action)
    {
        if (instance == null)
            return;
        await _graphicsService.TriggerInstanceAsync(instance.Name, action);
    }

    public bool IsProfileActive(string? profileName)
    {
        return !string.IsNullOrWhiteSpace(profileName)
            && string.Equals(SelectedProfileName, profileName, StringComparison.Ordinal);
    }

    public bool TryGetAtlasByName(string? atlasName, out GraphicsAtlasViewModel atlas)
    {
        atlas = null!;
        if (string.IsNullOrWhiteSpace(atlasName))
            return false;

        var match = Atlases.FirstOrDefault(a => string.Equals(a.Name, atlasName, StringComparison.Ordinal));
        if (match == null)
            return false;

        atlas = match;
        return true;
    }

    public bool TryGetInstanceByName(string? instanceName, out GraphicsInstanceViewModel instance)
    {
        instance = null!;
        if (string.IsNullOrWhiteSpace(instanceName))
            return false;

        var match = Instances.FirstOrDefault(i => string.Equals(i.Name, instanceName, StringComparison.Ordinal));
        if (match == null)
            return false;

        instance = match;
        return true;
    }

    public async Task ExecuteAtlasHotkeyActionAsync(string? atlasName, string? action)
    {
        if (!TryGetAtlasByName(atlasName, out var atlas) || string.IsNullOrWhiteSpace(action))
            return;

        switch (action)
        {
            case "reload":
                await ReloadAtlasAsync(atlas);
                break;
            case "anim_in":
                await TriggerAtlasAsync(atlas, "animIn");
                break;
            case "anim_out":
                await TriggerAtlasAsync(atlas, "animOut");
                break;
            case "toggle_instances_visible":
                await SetAtlasInstancesVisibleAsync(atlas, !atlas.InstancesVisible);
                break;
            case "toggle_enabled":
                atlas.Enabled = !atlas.Enabled;
                break;
        }
    }

    public async Task ExecuteInstanceHotkeyActionAsync(string? instanceName, string? action)
    {
        if (!TryGetInstanceByName(instanceName, out var instance) || string.IsNullOrWhiteSpace(action))
            return;

        switch (action)
        {
            case "anim_in":
                await TriggerInstanceAsync(instance, "animIn");
                break;
            case "anim_out":
                await TriggerInstanceAsync(instance, "animOut");
                break;
            case "toggle_visible":
                    await SetInstanceVisibleAsync(instance, !instance.LiveVisible);
                break;
        }
    }

    private void OnProfileChanged(object? sender, EventArgs e)
    {
        RefreshProfiles();
        RefreshFromProfile();
    }

    private void OnDirtyStateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnDirtyStateChanged(sender, e));
            return;
        }

        RefreshProfileDisplayNames();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private GraphicsAtlasViewModel CreateAtlasViewModel(GraphicsAtlas atlas)
    {
        var viewModel = new GraphicsAtlasViewModel(atlas, OnAtlasEnabledChanged, OnAtlasInstancesVisibleChanged);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(GraphicsAtlasViewModel.InstancesVisible))
                NotifyProfileEdited();
        };
        foreach (var region in viewModel.Regions)
            TrackRegionViewModel(region);
        return viewModel;
    }

    private GraphicsRegionViewModel CreateRegionViewModel(GraphicsRegion region)
    {
        var viewModel = new GraphicsRegionViewModel(region);
        TrackRegionViewModel(viewModel);
        return viewModel;
    }

    private void TrackRegionViewModel(GraphicsRegionViewModel viewModel)
    {
        viewModel.PropertyChanged += (_, _) => NotifyProfileEdited();
    }

    private GraphicsInstanceViewModel CreateInstanceViewModel(GraphicsInstance instance)
    {
        var viewModel = new GraphicsInstanceViewModel(instance, OnInstanceLiveVisibleChanged);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(GraphicsInstanceViewModel.LiveVisible))
                NotifyProfileEdited();
        };
        return viewModel;
    }

    private void NotifyProfileEdited()
    {
        _graphicsService.NotifyProfileEdited();
    }

    private void OnAtlasEnabledChanged(GraphicsAtlasViewModel atlas)
    {
    }

    private void OnInstanceLiveVisibleChanged(GraphicsInstanceViewModel instance)
    {
        if (_suppressApply)
            return;
        _ = _graphicsService.UpdateInstanceVisibilityAsync(instance.Name, instance.LiveVisible);
        if (instance.SourceType != GraphicsInstanceSourceType.Atlas)
            return;
        var atlas = Atlases.FirstOrDefault(a => a.Name == instance.Atlas);
        if (atlas != null)
        {
            UpdateAtlasInstancesVisibilityState(atlas);
        }
    }

    private void OnAtlasInstancesVisibleChanged(GraphicsAtlasViewModel atlas, bool visible)
    {
        if (_suppressApply)
            return;
        _ = SetAtlasInstancesVisibleAsync(atlas, visible);
    }

    private void OnInstancesVisibilityChanged(object? sender, GraphicsVisibilityEvent e)
    {
        if (e.InstanceNames.Count == 0)
            return;

        _suppressApply = true;
        try
        {
            foreach (var name in e.InstanceNames)
            {
                var vm = Instances.FirstOrDefault(inst => inst.Name == name);
                if (vm == null)
                    continue;
                vm.SetLiveVisibleInternal(e.Visible);
                if (vm.SourceType != GraphicsInstanceSourceType.Atlas)
                    continue;
                var atlas = Atlases.FirstOrDefault(a => a.Name == vm.Atlas);
                if (atlas != null)
                {
                    UpdateAtlasInstancesVisibilityState(atlas);
                }
            }
        }
        finally
        {
            _suppressApply = false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _toastCts?.Cancel();
        _toastCts?.Dispose();
        _graphicsService.ProfileChanged -= OnProfileChanged;
        _graphicsService.DirtyStateChanged -= OnDirtyStateChanged;
        _graphicsService.InstancesVisibilityChanged -= OnInstancesVisibilityChanged;
    }

    public void CreateProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        if (!_graphicsService.CreateProfile(name))
        {
            ShowToast("Choose a new, non-reserved profile name.");
            return;
        }

        RefreshProfiles();
        ShowToast("Empty profile created");
    }

    public void SaveEmptyProfileAs(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        if (!_graphicsService.SaveEmptyProfileAs(name))
        {
            ShowToast("Choose a new, non-reserved profile name.");
            return;
        }

        RefreshProfiles();
        ShowToast("Profile saved");
    }

    public void DuplicateCurrentProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        if (_graphicsService.HasUnsavedChanges && !_graphicsService.SaveCurrentProfile())
        {
            ShowToast("Profile could not be saved");
            return;
        }
        if (!_graphicsService.DuplicateCurrentProfile(name))
        {
            ShowToast("Choose a new, non-reserved profile name.");
            return;
        }

        RefreshProfiles();
        ShowToast("Profile duplicated");
    }

    public void RenameCurrentProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        if (!_graphicsService.RenameCurrentProfile(name))
        {
            ShowToast("Choose a new, non-reserved profile name.");
            return;
        }

        RefreshProfiles();
        ShowToast("Profile renamed");
    }

    public void SaveCurrentProfile()
    {
        ShowToast(_graphicsService.SaveCurrentProfile() ? "Profile saved" : "Profile could not be saved");
    }

    public void LoadProfile(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)
            || string.Equals(profileName, SelectedProfileName, StringComparison.OrdinalIgnoreCase))
        {
            RestoreSelectedProfile();
            return;
        }

        _graphicsService.LoadProfile(profileName);
    }

    public void RestoreSelectedProfile()
    {
        SelectedProfile = Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Name, SelectedProfileName, StringComparison.OrdinalIgnoreCase));
    }

    public void RemoveSelectedProfile()
    {
        if (string.IsNullOrWhiteSpace(SelectedProfileName))
            return;
        if (GraphicsProfileStorage.IsReservedProfileName(SelectedProfileName))
            return;

        var removedProfileName = SelectedProfileName;
        _graphicsService.DeleteProfile(SelectedProfileName);
        RefreshProfiles();
        SelectedProfileName = _graphicsService.CurrentProfileName;
        ProfileRemoved?.Invoke(this, removedProfileName);
        ShowToast("Profile removed");
    }

    private void RefreshProfiles()
    {
        _refreshingProfiles = true;
        try
        {
            var currentProfileName = _graphicsService.CurrentProfileName;
            var profiles = _graphicsService.ListProfiles();

            // Do not clear the collection: doing so invalidates ComboBox's selection model,
            // even when the selected profile still exists.
            for (var i = Profiles.Count - 1; i >= 0; i--)
            {
                if (!profiles.Any(name => string.Equals(name, Profiles[i].Name, StringComparison.OrdinalIgnoreCase)))
                    Profiles.RemoveAt(i);
            }

            foreach (var name in profiles)
            {
                if (!Profiles.Any(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)))
                    Profiles.Add(new GraphicsProfileListItem(name));
            }

            _selectedProfileName = profiles.FirstOrDefault(name =>
                    string.Equals(name, currentProfileName, StringComparison.OrdinalIgnoreCase))
                ?? GraphicsProfileStorage.EmptyProfileName;
            OnPropertyChanged(nameof(SelectedProfileName));
            OnPropertyChanged(nameof(CanRemoveSelectedProfile));
            OnPropertyChanged(nameof(IsEmptyProfile));
            OnPropertyChanged(nameof(CanManageSelectedProfile));
            RestoreSelectedProfile();
            RefreshProfileDisplayNames();
        }
        finally
        {
            _refreshingProfiles = false;
        }
    }

    private void RefreshProfileDisplayNames()
    {
        foreach (var profile in Profiles)
            profile.DisplayName = GetProfileDisplayName(profile.Name);
    }

    private string GetProfileDisplayName(string profileName)
    {
        return string.Equals(profileName, SelectedProfileName, StringComparison.OrdinalIgnoreCase)
            && _graphicsService.HasUnsavedChanges
            ? $"{profileName}*"
            : profileName;
    }

    private void ShowToast(string message)
    {
        if (_disposed)
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowToast(message));
            return;
        }

        _toastCts?.Cancel();
        _toastCts?.Dispose();
        _toastCts = new CancellationTokenSource();

        ToastMessage = message;
        IsToastVisible = true;
        _ = HideToastAfterDelayAsync(_toastCts.Token);
    }

    private async Task HideToastAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(2500, token);
            if (!token.IsCancellationRequested)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && !token.IsCancellationRequested)
                        IsToastVisible = false;
                });
            }
        }
        catch (TaskCanceledException)
        {
        }
    }


    public sealed class GraphicsProfileListItem : ViewModelBase
    {
        private string _displayName;

        public GraphicsProfileListItem(string name)
        {
            Name = name;
            _displayName = name;
        }

        public string Name { get; }

        public string DisplayName
        {
            get => _displayName;
            set => SetProperty(ref _displayName, value);
        }
    }

    public sealed class GraphicsAtlasViewModel : ViewModelBase
    {
        public GraphicsAtlas Model { get; }
        public ObservableCollection<GraphicsRegionViewModel> Regions { get; } = new();
        private readonly Action<GraphicsAtlasViewModel>? _enabledChanged;
        private readonly Action<GraphicsAtlasViewModel, bool>? _instancesVisibleChanged;
        private bool _instancesVisible;
        private bool _isRegionScope;

        public GraphicsAtlasViewModel(GraphicsAtlas model, Action<GraphicsAtlasViewModel>? enabledChanged, Action<GraphicsAtlasViewModel, bool>? instancesVisibleChanged)
        {
            Model = model;
            _enabledChanged = enabledChanged;
            _instancesVisibleChanged = instancesVisibleChanged;
            foreach (var region in model.Regions)
            {
                Regions.Add(new GraphicsRegionViewModel(region));
            }
        }

        public string Name
        {
            get => Model.Name;
            set { Model.Name = value; OnPropertyChanged(); }
        }

        public int Width
        {
            get => Model.Width;
            set { Model.Width = value; OnPropertyChanged(); }
        }

        public int Height
        {
            get => Model.Height;
            set { Model.Height = value; OnPropertyChanged(); }
        }

        public GraphicsAtlasFormat Format
        {
            get => Model.Format;
            set { Model.Format = value; OnPropertyChanged(); }
        }

        public GraphicsAlphaMode AlphaMode
        {
            get => Model.AlphaMode;
            set { Model.AlphaMode = value; OnPropertyChanged(); }
        }

        public bool KeyedMutex
        {
            get => Model.KeyedMutex;
            set { Model.KeyedMutex = value; OnPropertyChanged(); }
        }

        public string HtmlPath
        {
            get => Model.HtmlPath;
            set { Model.HtmlPath = value; OnPropertyChanged(); }
        }

        public bool Enabled
        {
            get => Model.Enabled;
            set
            {
                if (Model.Enabled == value)
                    return;
                Model.Enabled = value;
                OnPropertyChanged();
                _enabledChanged?.Invoke(this);
            }
        }

        public bool InstancesVisible
        {
            get => _instancesVisible;
            set
            {
                if (_instancesVisible == value)
                    return;
                _instancesVisible = value;
                OnPropertyChanged();
                _instancesVisibleChanged?.Invoke(this, value);
            }
        }

        public bool IsRegionScope
        {
            get => _isRegionScope;
            set
            {
                if (!SetProperty(ref _isRegionScope, value))
                    return;
                OnPropertyChanged(nameof(NavigationBackground));
            }
        }

        public string NavigationBackground => IsRegionScope ? "#303846" : "Transparent";

        public void SetInstancesVisibleInternal(bool visible)
        {
            if (_instancesVisible == visible)
                return;
            _instancesVisible = visible;
            OnPropertyChanged(nameof(InstancesVisible));
        }
    }

    public sealed class GraphicsRegionViewModel : ViewModelBase
    {
        public GraphicsRegion Model { get; }

        public GraphicsRegionViewModel(GraphicsRegion model)
        {
            Model = model;
        }

        public string Id
        {
            get => Model.Id;
            set { Model.Id = value; OnPropertyChanged(); }
        }

        public double U0
        {
            get => Model.U0;
            set { Model.U0 = value; OnPropertyChanged(); }
        }

        public double V0
        {
            get => Model.V0;
            set { Model.V0 = value; OnPropertyChanged(); }
        }

        public double U1
        {
            get => Model.U1;
            set { Model.U1 = value; OnPropertyChanged(); }
        }

        public double V1
        {
            get => Model.V1;
            set { Model.V1 = value; OnPropertyChanged(); }
        }

        public double DefaultWidth
        {
            get => Model.DefaultWidth;
            set { Model.DefaultWidth = value; OnPropertyChanged(); }
        }

        public double DefaultHeight
        {
            get => Model.DefaultHeight;
            set { Model.DefaultHeight = value; OnPropertyChanged(); }
        }
    }

    public sealed class GraphicsInstanceViewModel : ViewModelBase
    {
        public GraphicsInstance Model { get; }
        private readonly Action<GraphicsInstanceViewModel>? _liveVisibleChanged;
        private bool _liveVisible;

        public GraphicsInstanceViewModel(GraphicsInstance model, Action<GraphicsInstanceViewModel>? liveVisibleChanged)
        {
            Model = model;
            _liveVisibleChanged = liveVisibleChanged;
            _liveVisible = model.Visible;
        }

        public string Name
        {
            get => Model.Name;
            set { Model.Name = value; OnPropertyChanged(); }
        }

        public GraphicsInstanceSourceType SourceType
        {
            get => Model.SourceType;
            set
            {
                if (Model.SourceType == value)
                    return;
                Model.SourceType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SourceSummary));
            }
        }

        public string Atlas
        {
            get => Model.Atlas;
            set
            {
                Model.Atlas = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SourceSummary));
            }
        }

        public string Region
        {
            get => Model.Region;
            set
            {
                Model.Region = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SourceSummary));
            }
        }

        public string ImageFile
        {
            get => Model.ImageFile;
            set
            {
                Model.ImageFile = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SourceSummary));
            }
        }

        public string SourceSummary => SourceType == GraphicsInstanceSourceType.Image
            ? $"I: {ImageFile}"
            : $"A: {Atlas} / {Region}";

        public int AttachSlot
        {
            get => Model.AttachSlot;
            set { Model.AttachSlot = value; OnPropertyChanged(); }
        }

        public string AttachAttachmentName
        {
            get => Model.AttachAttachmentName;
            set { Model.AttachAttachmentName = value ?? string.Empty; OnPropertyChanged(); }
        }

        public string AttachBoneName
        {
            get => Model.AttachBoneName;
            set { Model.AttachBoneName = value ?? string.Empty; OnPropertyChanged(); }
        }

        public bool AttachUseYaw
        {
            get => Model.AttachUseYaw;
            set { Model.AttachUseYaw = value; OnPropertyChanged(); }
        }

        public bool AttachUsePitch
        {
            get => Model.AttachUsePitch;
            set { Model.AttachUsePitch = value; OnPropertyChanged(); }
        }

        public bool AttachUseRoll
        {
            get => Model.AttachUseRoll;
            set { Model.AttachUseRoll = value; OnPropertyChanged(); }
        }

        public double PosX
        {
            get => Model.PosX;
            set { Model.PosX = value; OnPropertyChanged(); }
        }

        public double PosY
        {
            get => Model.PosY;
            set { Model.PosY = value; OnPropertyChanged(); }
        }

        public double PosZ
        {
            get => Model.PosZ;
            set { Model.PosZ = value; OnPropertyChanged(); }
        }

        public double Pitch
        {
            get => Model.Pitch;
            set { Model.Pitch = value; OnPropertyChanged(); }
        }

        public double Yaw
        {
            get => Model.Yaw;
            set { Model.Yaw = value; OnPropertyChanged(); }
        }

        public double Roll
        {
            get => Model.Roll;
            set { Model.Roll = value; OnPropertyChanged(); }
        }

        public double ScaleX
        {
            get => Model.ScaleX;
            set { Model.ScaleX = value; OnPropertyChanged(); }
        }

        public double ScaleY
        {
            get => Model.ScaleY;
            set { Model.ScaleY = value; OnPropertyChanged(); }
        }

        public bool Visible
        {
            get => Model.Visible;
            set
            {
                if (Model.Visible == value)
                    return;
                Model.Visible = value;
                OnPropertyChanged();
            }
        }

        public bool LiveVisible
        {
            get => _liveVisible;
            set
            {
                if (_liveVisible == value)
                    return;
                _liveVisible = value;
                OnPropertyChanged();
                _liveVisibleChanged?.Invoke(this);
            }
        }

        public void SetLiveVisibleInternal(bool visible)
        {
            if (_liveVisible == visible)
                return;
            _liveVisible = visible;
            OnPropertyChanged(nameof(LiveVisible));
        }

        public bool DepthTest
        {
            get => Model.DepthTest;
            set { Model.DepthTest = value; OnPropertyChanged(); }
        }

        public bool DepthWrite
        {
            get => Model.DepthWrite;
            set { Model.DepthWrite = value; OnPropertyChanged(); }
        }
    }

    public sealed class GraphicsInstanceSourceOption
    {
        public GraphicsInstanceSourceOption(string label, GraphicsInstanceSourceType value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public GraphicsInstanceSourceType Value { get; }
    }

    public sealed class AttachSlotOption
    {
        public AttachSlotOption(string label, int value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public int Value { get; }
    }

    public sealed class AttachAttachmentOption
    {
        public AttachAttachmentOption(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public string Value { get; }
    }

    private sealed class Relay : ICommand
    {
        private readonly Func<bool>? _canExecute;
        private readonly Action _execute;

        public Relay(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => _execute();

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Relay<T> : ICommand
    {
        private readonly Func<T?, bool>? _canExecute;
        private readonly Action<T?> _execute;

        public Relay(Action<T?> execute, Func<T?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
        {
            if (parameter is T typed)
                return _canExecute?.Invoke(typed) ?? true;
            return _canExecute?.Invoke(default) ?? true;
        }

        public void Execute(object? parameter)
        {
            if (parameter is T typed)
            {
                _execute(typed);
            }
            else
            {
                _execute(default);
            }
        }

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
