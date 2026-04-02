using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.Graphics;
using HlaeObsTools.Services.Settings;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels.Docks;

public sealed class GraphicsDockViewModel : Tool, IDisposable
{
    private readonly GraphicsService _graphicsService;
    private readonly SettingsStorage _settingsStorage;
    private readonly AppSettingsData _settings;

    private bool _isSetupView;
    private bool _isEnabled;
    private GraphicsAtlasViewModel? _selectedAtlas;
    private GraphicsRegionViewModel? _selectedRegion;
    private GraphicsInstanceViewModel? _selectedInstance;
    private GraphicsAtlasViewModel? _selectedInstanceAtlas;
    private GraphicsRegionViewModel? _selectedInstanceRegion;
    private GraphicsInstanceSourceOption? _selectedInstanceSource;
    private string? _selectedInstanceImageFile;
    private AttachSlotOption? _selectedInstanceAttachSlot;
    private AttachAttachmentOption? _selectedInstanceAttachment;
    private string _selectedProfileName = "default";
    private string _statusText = "Idle";
    private bool _suppressApply;
    public event EventHandler<string>? ProfileRemoved;

    public ObservableCollection<GraphicsAtlasViewModel> Atlases { get; } = new();
    public ObservableCollection<GraphicsInstanceViewModel> Instances { get; } = new();
    public ObservableCollection<GraphicsInstanceSourceOption> InstanceSourceOptions { get; } = new();
    public ObservableCollection<string> AvailableImages { get; } = new();
    public ObservableCollection<AttachSlotOption> AttachSlotOptions { get; } = new();
    public ObservableCollection<AttachAttachmentOption> AttachAttachmentOptions { get; } = new();
    public ObservableCollection<string> Profiles { get; } = new();

    public GraphicsDockViewModel(GraphicsService graphicsService, SettingsStorage settingsStorage, AppSettingsData settings)
    {
        _graphicsService = graphicsService;
        _settingsStorage = settingsStorage;
        _settings = settings;

        Title = "Graphics";
        CanClose = false;
        CanFloat = true;
        CanPin = true;

        _isEnabled = settings.GraphicsEnabled;
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
        RefreshProfiles();
        SelectedProfileName = _graphicsService.CurrentProfileName;
        RefreshFromProfile();

        _graphicsService.ProfileChanged += OnProfileChanged;
        _graphicsService.InstancesVisibilityChanged += OnInstancesVisibilityChanged;

        ShowSetupCommand = new Relay(() => IsSetupView = true);
        ShowLiveCommand = new Relay(() => IsSetupView = false);
        ApplyCommand = new Relay(async () => await ApplyAsync());
        SaveProfileCommand = new Relay(() => _graphicsService.SaveProfile(_selectedProfileName));
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
        RemoveAtlasCommand = new Relay(() =>
        {
            var atlas = SelectedAtlas;
            if (atlas == null) return;
            Atlases.Remove(atlas);
            _graphicsService.Profile.Atlases.Remove(atlas.Model);
            SelectedAtlas = Atlases.FirstOrDefault();
        });
        AddRegionCommand = new Relay(AddRegion);
        RemoveRegionCommand = new Relay(() =>
        {
            var atlas = SelectedAtlas;
            var region = SelectedRegion;
            if (atlas == null || region == null) return;
            atlas.Regions.Remove(region);
            atlas.Model.Regions.Remove(region.Model);
            SelectedRegion = atlas.Regions.FirstOrDefault();
        });
        AddInstanceCommand = new Relay(AddInstance);
        RemoveInstanceCommand = new Relay(() =>
        {
            var instance = SelectedInstance;
            if (instance == null) return;
            Instances.Remove(instance);
            _graphicsService.Profile.Instances.Remove(instance.Model);
            SelectedInstance = Instances.FirstOrDefault();
        });

        _ = RefreshAvailableImagesAsync();
    }

    public bool IsSetupView
    {
        get => _isSetupView;
        set => SetProperty(ref _isSetupView, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value))
                return;
            _settings.GraphicsEnabled = value;
            _settingsStorage.Save(_settings);
            _graphicsService.SetEnabled(value);
        }
    }

    public GraphicsAtlasViewModel? SelectedAtlas
    {
        get => _selectedAtlas;
        set
        {
            if (!SetProperty(ref _selectedAtlas, value))
                return;
            SelectedRegion = _selectedAtlas?.Regions.FirstOrDefault();
        }
    }

    public GraphicsRegionViewModel? SelectedRegion
    {
        get => _selectedRegion;
        set => SetProperty(ref _selectedRegion, value);
    }

    public GraphicsInstanceViewModel? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!SetProperty(ref _selectedInstance, value))
                return;
            SelectedInstanceSource = ResolveInstanceSource(_selectedInstance?.SourceType ?? GraphicsInstanceSourceType.Atlas);
            SelectedInstanceAtlas = ResolveAtlasByName(_selectedInstance?.Atlas);
            _selectedInstanceImageFile = ResolveImageFile(_selectedInstance?.ImageFile);
            OnPropertyChanged(nameof(SelectedInstanceImageFile));
            SelectedInstanceAttachSlot = ResolveAttachSlot(_selectedInstance?.AttachSlot ?? -1);
            SelectedInstanceAttachment = ResolveAttachmentName(_selectedInstance?.AttachAttachmentName);
            SelectedInstanceRegion = ResolveRegionById(SelectedInstanceAtlas, _selectedInstance?.Region);
        }
    }

    public GraphicsInstanceSourceOption? SelectedInstanceSource
    {
        get => _selectedInstanceSource;
        set
        {
            if (!SetProperty(ref _selectedInstanceSource, value))
                return;
            if (SelectedInstance != null)
            {
                SelectedInstance.SourceType = value?.Value ?? GraphicsInstanceSourceType.Atlas;
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
            if (!SetProperty(ref _selectedInstanceAtlas, value))
                return;
            if (SelectedInstance != null)
            {
                SelectedInstance.Atlas = value?.Name ?? string.Empty;
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
            if (SelectedInstance != null)
            {
                SelectedInstance.ImageFile = value ?? string.Empty;
            }
        }
    }

    public GraphicsRegionViewModel? SelectedInstanceRegion
    {
        get => _selectedInstanceRegion;
        set
        {
            if (!SetProperty(ref _selectedInstanceRegion, value))
                return;
            if (SelectedInstance != null)
            {
                SelectedInstance.Region = value?.Id ?? string.Empty;
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
            if (SelectedInstance != null)
            {
                SelectedInstance.AttachSlot = value?.Value ?? -1;
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
            if (SelectedInstance != null)
            {
                SelectedInstance.AttachAttachmentName = value?.Value ?? string.Empty;
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string SelectedProfileName
    {
        get => _selectedProfileName;
        set
        {
            if (!SetProperty(ref _selectedProfileName, value))
                return;
            if (string.IsNullOrWhiteSpace(value))
                return;
            _graphicsService.LoadProfile(value);
        }
    }

    public bool IsAtlasSourceSelected => SelectedInstanceSource?.Value == GraphicsInstanceSourceType.Atlas;

    public bool IsImageSourceSelected => SelectedInstanceSource?.Value == GraphicsInstanceSourceType.Image;

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
    public ICommand AddAtlasCommand { get; }
    public ICommand RemoveAtlasCommand { get; }
    public ICommand AddRegionCommand { get; }
    public ICommand RemoveRegionCommand { get; }
    public ICommand AddInstanceCommand { get; }
    public ICommand RemoveInstanceCommand { get; }

    private async Task ApplyAsync()
    {
        StatusText = "Applying...";
        await _graphicsService.ApplyProfileAsync();
        StatusText = "Applied";
    }

    private async Task ReloadAllAsync()
    {
        foreach (var atlas in Atlases)
        {
            await _graphicsService.ReloadAtlasAsync(atlas.Name);
        }
        StatusText = "Reloaded";
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
        var vm = new GraphicsAtlasViewModel(atlas, OnAtlasEnabledChanged, OnAtlasInstancesVisibleChanged);
        Atlases.Add(vm);
        SelectedAtlas = vm;
    }

    public void AddRegion(string id)
    {
        if (SelectedAtlas == null || string.IsNullOrWhiteSpace(id))
            return;
        id = MakeUnique(id.Trim(), n => SelectedAtlas.Regions.Any(r => r.Id == n));
        var region = new GraphicsRegion { Id = id };
        SelectedAtlas.Model.Regions.Add(region);
        var vm = new GraphicsRegionViewModel(region);
        SelectedAtlas.Regions.Add(vm);
        SelectedRegion = vm;
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
        var vm = new GraphicsInstanceViewModel(instance, OnInstanceVisibleChanged);
        Instances.Add(vm);
        SelectedInstance = vm;
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

    private void RefreshFromProfile()
    {
        _suppressApply = true;
        Atlases.Clear();
        foreach (var atlas in _graphicsService.Profile.Atlases)
        {
            Atlases.Add(new GraphicsAtlasViewModel(atlas, OnAtlasEnabledChanged, OnAtlasInstancesVisibleChanged));
        }
        Instances.Clear();
        foreach (var inst in _graphicsService.Profile.Instances)
        {
            Instances.Add(new GraphicsInstanceViewModel(inst, OnInstanceVisibleChanged));
        }

        foreach (var atlas in Atlases)
        {
            UpdateAtlasInstancesVisibilityState(atlas);
        }

        SelectedAtlas = Atlases.FirstOrDefault();
        SelectedInstance = Instances.FirstOrDefault();
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
        if (string.IsNullOrWhiteSpace(name))
            return Atlases.FirstOrDefault();
        return Atlases.FirstOrDefault(atlas => atlas.Name == name) ?? Atlases.FirstOrDefault();
    }

    private static GraphicsRegionViewModel? ResolveRegionById(GraphicsAtlasViewModel? atlas, string? id)
    {
        if (atlas == null)
            return null;
        if (string.IsNullOrWhiteSpace(id))
            return atlas.Regions.FirstOrDefault();
        return atlas.Regions.FirstOrDefault(region => region.Id == id) ?? atlas.Regions.FirstOrDefault();
    }

    private GraphicsInstanceSourceOption? ResolveInstanceSource(GraphicsInstanceSourceType sourceType)
    {
        return InstanceSourceOptions.FirstOrDefault(option => option.Value == sourceType) ?? InstanceSourceOptions.FirstOrDefault();
    }

    private string? ResolveImageFile(string? imageFile)
    {
        if (string.IsNullOrWhiteSpace(imageFile))
            return AvailableImages.FirstOrDefault();
        return AvailableImages.FirstOrDefault(image => string.Equals(image, imageFile, StringComparison.OrdinalIgnoreCase)) ?? imageFile;
    }

    private AttachSlotOption? ResolveAttachSlot(int slot)
    {
        return AttachSlotOptions.FirstOrDefault(option => option.Value == slot) ?? AttachSlotOptions.FirstOrDefault();
    }

    private AttachAttachmentOption? ResolveAttachmentName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return AttachAttachmentOptions.FirstOrDefault();
        return AttachAttachmentOptions.FirstOrDefault(option => option.Value == name) ?? AttachAttachmentOptions.FirstOrDefault();
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
        {
            StatusText = "Camera unavailable";
            return;
        }

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

        StatusText = copyPosition && copyRotation
            ? "Camera transform captured"
            : copyPosition
                ? "Camera position captured"
                : "Camera rotation captured";
    }

    private async Task SetAllInstancesVisibleAsync(bool visible)
    {
        _suppressApply = true;
        foreach (var inst in Instances)
        {
            inst.Visible = visible;
        }
        foreach (var atlas in Atlases)
        {
            UpdateAtlasInstancesVisibilityState(atlas);
        }
        _suppressApply = false;
        await _graphicsService.UpdateInstancesVisibilityAsync(_graphicsService.Profile.Instances, visible);
    }

    private async Task SetAtlasInstancesVisibleAsync(GraphicsAtlasViewModel atlas, bool visible)
    {
        var related = Instances
            .Where(inst => inst.SourceType == GraphicsInstanceSourceType.Atlas && inst.Atlas == atlas.Name)
            .ToList();
        _suppressApply = true;
        foreach (var inst in related)
        {
            inst.Visible = visible;
        }
        _suppressApply = false;
        atlas.SetInstancesVisibleInternal(visible);
        await _graphicsService.UpdateInstancesVisibilityAsync(related.Select(r => r.Model), visible);
    }

    private async Task SetInstanceVisibleAsync(GraphicsInstanceViewModel instance, bool visible)
    {
        _suppressApply = true;
        instance.Visible = visible;
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
        var allVisible = related.All(inst => inst.Visible);
        atlas.SetInstancesVisibleInternal(allVisible);
    }

    private async Task ReloadAtlasAsync(GraphicsAtlasViewModel? atlas)
    {
        if (atlas == null)
            return;
        await _graphicsService.ReloadAtlasAsync(atlas.Name);
        StatusText = "Reloaded";
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
                await SetInstanceVisibleAsync(instance, !instance.Visible);
                break;
        }
    }

    private void OnProfileChanged(object? sender, EventArgs e)
    {
        _selectedProfileName = _graphicsService.CurrentProfileName;
        OnPropertyChanged(nameof(SelectedProfileName));
        RefreshFromProfile();
    }

    private void OnAtlasEnabledChanged(GraphicsAtlasViewModel atlas)
    {
        if (_suppressApply)
            return;
        _ = ApplyAsync();
    }

    private void OnInstanceVisibleChanged(GraphicsInstanceViewModel instance)
    {
        if (_suppressApply)
            return;
        _ = _graphicsService.UpdateInstanceVisibilityAsync(instance.Name, instance.Visible);
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
                vm.SetVisibleInternal(e.Visible);
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
        _graphicsService.ProfileChanged -= OnProfileChanged;
        _graphicsService.InstancesVisibilityChanged -= OnInstancesVisibilityChanged;
    }

    public void SaveProfileAs(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        _graphicsService.SaveProfile(name);
        RefreshProfiles();
        SelectedProfileName = _graphicsService.CurrentProfileName;
    }

    public void RemoveSelectedProfile()
    {
        if (string.IsNullOrWhiteSpace(SelectedProfileName))
            return;
        var removedProfileName = SelectedProfileName;
        _graphicsService.DeleteProfile(SelectedProfileName);
        RefreshProfiles();
        SelectedProfileName = _graphicsService.CurrentProfileName;
        ProfileRemoved?.Invoke(this, removedProfileName);
    }

    private void RefreshProfiles()
    {
        Profiles.Clear();
        var profiles = _graphicsService.ListProfiles();
        if (profiles.Length == 0)
        {
            Profiles.Add("default");
            return;
        }
        foreach (var name in profiles)
        {
            Profiles.Add(name);
        }
        if (!Profiles.Contains("default"))
            Profiles.Insert(0, "default");
    }


    public sealed class GraphicsAtlasViewModel : ViewModelBase
    {
        public GraphicsAtlas Model { get; }
        public ObservableCollection<GraphicsRegionViewModel> Regions { get; } = new();
        private readonly Action<GraphicsAtlasViewModel>? _enabledChanged;
        private readonly Action<GraphicsAtlasViewModel, bool>? _instancesVisibleChanged;
        private bool _instancesVisible;

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
        private readonly Action<GraphicsInstanceViewModel>? _visibleChanged;

        public GraphicsInstanceViewModel(GraphicsInstance model, Action<GraphicsInstanceViewModel>? visibleChanged)
        {
            Model = model;
            _visibleChanged = visibleChanged;
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
                _visibleChanged?.Invoke(this);
            }
        }

        public void SetVisibleInternal(bool visible)
        {
            if (Model.Visible == visible)
                return;
            Model.Visible = visible;
            OnPropertyChanged(nameof(Visible));
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
