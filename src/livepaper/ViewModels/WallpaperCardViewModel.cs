using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using livepaper.Helpers;
using livepaper.Models;

namespace livepaper.ViewModels;

public partial class WallpaperCardViewModel : ViewModelBase
{
    public string Title { get; }
    public string ThumbnailSource { get; }
    public string PageUrl { get; }
    public string? Resolution { get; }
    public LibraryItem? LibraryItem { get; }
    public bool IsScene { get; }
    public string? WorkshopId { get; }
    public bool IsGifThumbnail => ThumbnailSource.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    private Avalonia.Labs.Gif.IGifSource? _gifSource;
    public Avalonia.Labs.Gif.IGifSource? GifSource => _gifSource ??= LoadGifSource();

    [ObservableProperty] private bool _isGifActive;
    partial void OnIsGifActiveChanged(bool value) => OnPropertyChanged(nameof(ActiveGifSource));
    public Avalonia.Labs.Gif.IGifSource? ActiveGifSource => IsGifActive ? GifSource : null;

    private Avalonia.Labs.Gif.IGifSource? LoadGifSource()
    {
        if (!IsGifThumbnail) return null;
        try
        {
            if (ThumbnailSource.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return Avalonia.Labs.Gif.GifStreamSource.FromUriString(ThumbnailSource);

            string path = ThumbnailSource;
            if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(7);

            if (File.Exists(path))
                return Avalonia.Labs.Gif.GifStreamSource.FromStream(File.OpenRead(path));
        }
        catch { }
        return null;
    }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isInPlaylist;
    [ObservableProperty] private bool _hasCrashed;
    [ObservableProperty] private bool _isWhitelisted;
    [ObservableProperty] private int _sliderVolume;
    [ObservableProperty] private double _sliderSpeed;

    private int? _volumeOverride;
    private int _globalVolume;
    private bool _suppressSliderChange;

    private double? _speedOverride;
    private double _globalSpeed;
    private bool _suppressSpeedChange;

    public bool IsVolumeSynced => _volumeOverride == null;
    public int? VolumeOverride => _volumeOverride;
    public bool IsSpeedSynced => _speedOverride == null;
    public double? SpeedOverride => _speedOverride;

    partial void OnIsWhitelistedChanged(bool value)
    {
        if (LibraryItem != null) LibraryService.SetWhitelisted(LibraryItem.VideoPath, value);
    }

    partial void OnSliderVolumeChanged(int value)
    {
        if (_suppressSliderChange || LibraryItem == null) return;
        _volumeOverride = value;
        OnPropertyChanged(nameof(IsVolumeSynced));
        OnPropertyChanged(nameof(VolumeOverride));
        LibraryService.SaveVolumeOverride(LibraryItem.VideoPath, value);
        if (IsCurrentlyPlaying)
            Task.Run(() => PlayerHelper.SetVolume(value));
        OnVolumeChanged?.Invoke(this, value);
    }

    [RelayCommand]
    private void SyncToGlobal()
    {
        if (LibraryItem == null) return;
        _volumeOverride = null;
        _suppressSliderChange = true;
        SliderVolume = _globalVolume;
        _suppressSliderChange = false;
        OnPropertyChanged(nameof(IsVolumeSynced));
        OnPropertyChanged(nameof(VolumeOverride));
        LibraryService.SaveVolumeOverride(LibraryItem.VideoPath, null);
        if (IsCurrentlyPlaying)
            Task.Run(() => PlayerHelper.SetVolume(_globalVolume));
        OnVolumeChanged?.Invoke(this, null);
    }

    public void UpdateGlobalVolume(int volume)
    {
        _globalVolume = volume;
        if (_volumeOverride == null)
        {
            _suppressSliderChange = true;
            SliderVolume = volume;
            _suppressSliderChange = false;
        }
    }

    partial void OnSliderSpeedChanged(double value)
    {
        if (_suppressSpeedChange || LibraryItem == null) return;
        _speedOverride = value;
        OnPropertyChanged(nameof(IsSpeedSynced));
        OnPropertyChanged(nameof(SpeedOverride));
        LibraryService.SaveSpeedOverride(LibraryItem.VideoPath, value);
        if (IsCurrentlyPlaying)
            Task.Run(() => PlayerHelper.SetSpeed(value));
        OnSpeedChanged?.Invoke(this, value);
    }

    [RelayCommand]
    private void SyncSpeedToGlobal()
    {
        if (LibraryItem == null) return;
        _speedOverride = null;
        _suppressSpeedChange = true;
        SliderSpeed = _globalSpeed;
        _suppressSpeedChange = false;
        OnPropertyChanged(nameof(IsSpeedSynced));
        OnPropertyChanged(nameof(SpeedOverride));
        LibraryService.SaveSpeedOverride(LibraryItem.VideoPath, null);
        if (IsCurrentlyPlaying)
            Task.Run(() => PlayerHelper.SetSpeed(_globalSpeed));
        OnSpeedChanged?.Invoke(this, null);
    }

    public void UpdateGlobalSpeed(double speed)
    {
        _globalSpeed = speed;
        if (_speedOverride == null)
        {
            _suppressSpeedChange = true;
            SliderSpeed = speed;
            _suppressSpeedChange = false;
        }
    }

    public Action<WallpaperCardViewModel, int?>? OnVolumeChanged { get; set; }
    public Action<WallpaperCardViewModel, double?>? OnSpeedChanged { get; set; }

    [ObservableProperty] private bool _isCurrentlyPlaying;

    public string CheckmarkText => IsInPlaylist ? "−" : "+";

    partial void OnIsInPlaylistChanged(bool value) => OnPropertyChanged(nameof(CheckmarkText));

    public Action<WallpaperCardViewModel>? OnTogglePlaylist { get; set; }

    [RelayCommand]
    private void AddToPlaylist() => OnTogglePlaylist?.Invoke(this);

    public WallpaperCardViewModel(WallpaperResult result)
    {
        Title = result.Title;
        ThumbnailSource = result.ThumbnailUrl;
        PageUrl = result.PageUrl;
        Resolution = result.Resolution;
        IsScene = result.IsScene;
        WorkshopId = result.WorkshopId;
    }

    public WallpaperCardViewModel(LibraryItem item)
    {
        Title = item.Title;
        ThumbnailSource = item.ThumbnailPath ?? "";
        PageUrl = item.VideoPath;
        LibraryItem = item;
        IsScene = item.IsScene;
        WorkshopId = item.WorkshopId;
#pragma warning disable MVVMTK0034
        _hasCrashed = item.HasCrashed;
        _isWhitelisted = item.IsWhitelisted;
        _volumeOverride = item.VolumeOverride;
        _sliderVolume = item.VolumeOverride ?? 0;
        _speedOverride = item.SpeedOverride;
        _sliderSpeed = item.SpeedOverride ?? 1.0;
#pragma warning restore MVVMTK0034
    }
}
