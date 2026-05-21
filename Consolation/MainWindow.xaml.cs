using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage;

namespace Consolation
{
    public sealed partial class MainWindow : Window
    {
        private const string SettingsFileName = "settings.json";

        private readonly DispatcherTimer _controlsHideTimer = new()
        {
            Interval = TimeSpan.FromSeconds(3)
        };

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true
        };

        private AppSettings _settings = new();
        private DeviceWatcher? _deviceWatcher;
        private MediaCapture? _mediaCapture;
        private MediaFrameSource? _previewFrameSource;
        private MediaPlayer? _mediaPlayer;
        private readonly List<CaptureDeviceOption> _captureDevices = [];
        private CaptureDeviceOption? _selectedDevice;
        private VideoModeOption? _selectedVideoMode;
        private bool _isPlaybackActive;
        private bool _isLoadingDevices;
        private bool _isControlsPointerOver;
        private bool _isDraggingControls;
        private bool _isSettingsDialogOpen;
        private bool _isMuted;
        private Point _dragPointerOffset;
        private double _previousVolume = 70;
        private TaskCompletionSource? _modalCompletionSource;

        public MainWindow()
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            MaximizeWindow();
            _controlsHideTimer.Tick += ControlsHideTimer_Tick;
            _ = InitializeCaptureDevicesAsync();
        }

        private async Task LoadSettingsAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(SettingsFileName);
                string json = await FileIO.ReadTextAsync(file);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                _settings.VideoStatsPosition ??= "BottomLeft";
                _settings.DeviceVideoModes ??= [];
            }
            catch (FileNotFoundException)
            {
                await SaveSettingsAsync();
            }
            catch
            {
                _settings = new AppSettings();
            }
        }

        private async Task SaveSettingsAsync()
        {
            StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                SettingsFileName,
                CreationCollisionOption.ReplaceExisting);

            string json = JsonSerializer.Serialize(_settings, _jsonOptions);
            await FileIO.WriteTextAsync(file, json);
        }

        private async Task InitializeCaptureDevicesAsync()
        {
            await LoadSettingsAsync();
            await RefreshCaptureDevicesAsync();
            StartDeviceWatcher();
        }

        private async Task RefreshCaptureDevicesAsync()
        {
            _isLoadingDevices = true;
            string? previouslySelectedDeviceId = _selectedDevice?.Id;

            DeviceComboBox.Items.Clear();
            _captureDevices.Clear();
            _selectedDevice = null;
            _selectedVideoMode = null;
            ResolutionMenu.Items.Clear();

            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            foreach (DeviceInformation device in devices.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                CaptureDeviceOption? option = await TryCreateCaptureDeviceOptionAsync(device);
                if (option is null || option.VideoModes.Count == 0)
                {
                    continue;
                }

                _captureDevices.Add(option);
                DeviceComboBox.Items.Add(option.Name);
            }

            if (_captureDevices.Count == 0)
            {
                DeviceComboBox.Items.Add("No Capture Cards found");
                DeviceComboBox.SelectedIndex = 0;
                DeviceComboBox.IsEnabled = false;
                VideoModeMenuBar.IsEnabled = false;
                ResolutionMenu.Title = "No video modes";
                SelectedModeText.Text = "Connect a capture card to choose a mode.";
                PlayButton.IsEnabled = false;
                _isLoadingDevices = false;
                return;
            }

            DeviceComboBox.IsEnabled = true;
            int preferredDeviceIndex = 0;
            if (previouslySelectedDeviceId is not null)
            {
                int existingDeviceIndex = _captureDevices.FindIndex(device => device.Id == previouslySelectedDeviceId);
                preferredDeviceIndex = Math.Max(0, existingDeviceIndex);
            }

            DeviceComboBox.SelectedIndex = preferredDeviceIndex;
            _isLoadingDevices = false;
            await SelectDeviceAsync(_captureDevices[preferredDeviceIndex]);
        }

        private static async Task<CaptureDeviceOption?> TryCreateCaptureDeviceOptionAsync(DeviceInformation device)
        {
            MediaCapture? probeCapture = null;

            try
            {
                probeCapture = new MediaCapture();
                await probeCapture.InitializeAsync(CreateMediaCaptureSettings(device.Id, MediaCaptureSharingMode.SharedReadOnly));

                IReadOnlyList<IMediaEncodingProperties> previewProperties =
                    probeCapture.VideoDeviceController.GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview);

                List<VideoModeOption> videoModes = previewProperties
                    .OfType<VideoEncodingProperties>()
                    .Where(properties => properties.Width > 0 && properties.Height > 0 && properties.FrameRate.Denominator > 0)
                    .Select(VideoModeOption.FromProperties)
                    .GroupBy(mode => mode.SettingsKey, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderBy(mode => FormatRank(mode.PixelFormat)).First())
                    .OrderByDescending(mode => mode.Width)
                    .ThenByDescending(mode => mode.Height)
                    .ThenByDescending(mode => mode.FrameRate)
                    .ThenBy(mode => FormatRank(mode.PixelFormat))
                    .ToList();

                return new CaptureDeviceOption(device.Id, device.Name, videoModes);
            }
            catch
            {
                return null;
            }
            finally
            {
                probeCapture?.Dispose();
            }
        }

        private static MediaCaptureInitializationSettings CreateMediaCaptureSettings(
            string deviceId,
            MediaCaptureSharingMode sharingMode = MediaCaptureSharingMode.ExclusiveControl)
        {
            return new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = sharingMode,
                MemoryPreference = MediaCaptureMemoryPreference.Auto
            };
        }

        private void StartDeviceWatcher()
        {
            _deviceWatcher = DeviceInformation.CreateWatcher(DeviceClass.VideoCapture);
            _deviceWatcher.Added += (_, _) => QueueDeviceRefresh();
            _deviceWatcher.Removed += (_, update) => QueueDeviceRefresh(update.Id);
            _deviceWatcher.Updated += (_, _) => QueueDeviceRefresh();
            _deviceWatcher.Start();
        }

        private void QueueDeviceRefresh(string? removedDeviceId = null)
        {
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                if (_isPlaybackActive)
                {
                    if (removedDeviceId == _selectedDevice?.Id)
                    {
                        await StopPlaybackAsync();
                    }
                    else
                    {
                        return;
                    }
                }

                await RefreshCaptureDevicesAsync();
            });
        }

        private async Task SelectDeviceAsync(CaptureDeviceOption device)
        {
            _selectedDevice = device;
            VideoModeOption? mode = TryGetSavedMode(device) ?? ChooseDefaultMode(device.VideoModes);
            BuildVideoModeMenu(device.VideoModes, mode);
            await SelectVideoModeAsync(mode, saveSelection: false);
        }

        private VideoModeOption? TryGetSavedMode(CaptureDeviceOption device)
        {
            if (!_settings.DeviceVideoModes.TryGetValue(device.Id, out SavedVideoMode? savedMode))
            {
                return null;
            }

            return device.VideoModes.FirstOrDefault(mode =>
                mode.Width == savedMode.Width &&
                mode.Height == savedMode.Height &&
                Math.Abs(mode.FrameRate - savedMode.FrameRate) < 0.01 &&
                string.Equals(mode.PixelFormat, savedMode.PixelFormat, StringComparison.OrdinalIgnoreCase));
        }

        private static VideoModeOption? ChooseDefaultMode(IReadOnlyList<VideoModeOption> modes)
        {
            VideoModeOption? preferred1080p = ChooseBestFormat(modes.Where(mode => mode.Width == 1920 && mode.Height == 1080 && IsSixtyFps(mode)));
            if (preferred1080p is not null)
            {
                return preferred1080p;
            }

            VideoModeOption? preferred720p = ChooseBestFormat(modes.Where(mode => mode.Width == 1280 && mode.Height == 720 && IsSixtyFps(mode)));
            if (preferred720p is not null)
            {
                return preferred720p;
            }

            VideoModeOption? highestResolution60p = ChooseBestFormat(modes
                .Where(IsSixtyFps)
                .OrderByDescending(mode => mode.Width * mode.Height)
                .ThenByDescending(mode => mode.Width));
            if (highestResolution60p is not null)
            {
                return highestResolution60p;
            }

            double highestFrameRate = modes.Count == 0 ? 0 : modes.Max(mode => mode.FrameRate);
            return ChooseBestFormat(modes
                .Where(mode => Math.Abs(mode.FrameRate - highestFrameRate) < 0.01)
                .OrderByDescending(mode => mode.Width * mode.Height)
                .ThenByDescending(mode => mode.Width));
        }

        private static VideoModeOption? ChooseBestFormat(IEnumerable<VideoModeOption> modes)
        {
            return modes
                .OrderBy(mode => FormatRank(mode.PixelFormat))
                .ThenByDescending(mode => mode.Width * mode.Height)
                .ThenByDescending(mode => mode.FrameRate)
                .FirstOrDefault();
        }

        private static bool IsSixtyFps(VideoModeOption mode)
        {
            return Math.Abs(mode.FrameRate - 60) < 0.75;
        }

        private static int FormatRank(string pixelFormat)
        {
            string normalized = NormalizePixelFormat(pixelFormat);
            return normalized switch
            {
                "YUY2" or "YUYV" => 0,
                "NV12" => 1,
                "UYVY" => 2,
                "RGB24" or "RGB32" or "ARGB32" or "BGRA8" => 3,
                "MJPEG" or "MJPG" => 20,
                _ => 10
            };
        }

        private void BuildVideoModeMenu(IReadOnlyList<VideoModeOption> modes, VideoModeOption? selectedMode)
        {
            ResolutionMenu.Items.Clear();

            foreach (IGrouping<string, VideoModeOption> resolutionGroup in modes.GroupBy(mode => mode.ResolutionLabel))
            {
                MenuFlyoutSubItem resolutionItem = new()
                {
                    Text = resolutionGroup.Key
                };

                foreach (IGrouping<string, VideoModeOption> frameRateGroup in resolutionGroup.GroupBy(mode => mode.FrameRateLabel))
                {
                    MenuFlyoutSubItem frameRateItem = new()
                    {
                        Text = frameRateGroup.Key
                    };

                    foreach (VideoModeOption mode in frameRateGroup.OrderBy(mode => FormatRank(mode.PixelFormat)))
                    {
                        ToggleMenuFlyoutItem formatItem = new()
                        {
                            Text = NormalizePixelFormat(mode.PixelFormat),
                            Tag = mode,
                            IsChecked = selectedMode?.SettingsKey == mode.SettingsKey
                        };
                        formatItem.Click += VideoModeMenuItem_Click;
                        frameRateItem.Items.Add(formatItem);
                    }

                    resolutionItem.Items.Add(frameRateItem);
                }

                ResolutionMenu.Items.Add(resolutionItem);
            }

            VideoModeMenuBar.IsEnabled = modes.Count > 0;
            PlayButton.IsEnabled = _selectedDevice is not null && selectedMode is not null;
        }

        private async Task SelectVideoModeAsync(VideoModeOption? mode, bool saveSelection)
        {
            _selectedVideoMode = mode;

            if (mode is null)
            {
                ResolutionMenu.Title = "No video modes";
                SelectedModeText.Text = "No compatible preview modes found.";
                PlayButton.IsEnabled = false;
                return;
            }

            ResolutionMenu.Title = $"{mode.Width}x{mode.Height} @ {mode.FrameRateLabel.Replace(" FPS", "p")}";
            SelectedModeText.Text = $"Selected: {mode.ResolutionLabel}, {mode.FrameRateLabel}, {NormalizePixelFormat(mode.PixelFormat)}";
            PlayButton.IsEnabled = _selectedDevice is not null;

            if (saveSelection && _selectedDevice is not null)
            {
                _settings.DeviceVideoModes[_selectedDevice.Id] = SavedVideoMode.FromMode(mode);
                await SaveSettingsAsync();
            }
        }

        private void MaximizeWindow()
        {
            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
            if (AppWindow.GetFromWindowId(windowId)?.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice is null || _selectedVideoMode is null)
            {
                return;
            }

            _isPlaybackActive = true;
            StartupForm.Visibility = Visibility.Collapsed;
            PlaybackViewer.Visibility = Visibility.Visible;
            ConnectingOverlay.Visibility = Visibility.Visible;
            ControlsLayer.Visibility = Visibility.Visible;
            PositionPlaybackControlsBottomCenter();
            ShowPlaybackControls(restartTimer: true);

            try
            {
                _mediaCapture = new MediaCapture();
                await _mediaCapture.InitializeAsync(CreateMediaCaptureSettings(_selectedDevice.Id));

                (_previewFrameSource, MediaFrameFormat? selectedFormat) = FindPlaybackFrameSource(_mediaCapture, _selectedVideoMode);
                if (_previewFrameSource is null)
                {
                    throw new InvalidOperationException(CreateMissingPlaybackStreamMessage(_mediaCapture));
                }

                if (selectedFormat is not null)
                {
                    await _previewFrameSource.SetFormatAsync(selectedFormat);
                }
                else
                {
                    await _mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(
                        _previewFrameSource.Info.MediaStreamType,
                        _selectedVideoMode.Properties);
                }

                _mediaPlayer = new MediaPlayer
                {
                    AutoPlay = false,
                    RealTimePlayback = true,
                    Source = MediaSource.CreateFromMediaFrameSource(_previewFrameSource)
                };
                _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
                CapturePreviewElement.SetMediaPlayer(_mediaPlayer);
                _mediaPlayer.Play();
                ConnectingOverlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                await StopPlaybackAsync();
                await ShowModalAsync(
                    "Capture Card Error",
                    new TextBlock
                    {
                        Text = $"The selected capture card could not start preview.\n\n{ex.Message}",
                        TextWrapping = TextWrapping.Wrap
                    });
            }
        }

        private static (MediaFrameSource? Source, MediaFrameFormat? Format) FindPlaybackFrameSource(MediaCapture mediaCapture, VideoModeOption mode)
        {
            List<MediaFrameSource> frameSources = mediaCapture.FrameSources
                .Where(source => source.Value.Info.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord)
                .Select(source => source.Value)
                .ToList();

            foreach (MediaFrameSource source in frameSources.OrderBy(SourceRank))
            {
                MediaFrameFormat? matchingFormat = FindMatchingFrameFormat(source, mode);
                if (matchingFormat is not null)
                {
                    return (source, matchingFormat);
                }
            }

            MediaFrameSource? fallbackSource = frameSources
                .OrderBy(SourceRank)
                .FirstOrDefault();

            return (fallbackSource, null);
        }

        private static int SourceRank(MediaFrameSource source)
        {
            int streamRank = source.Info.MediaStreamType == MediaStreamType.VideoPreview ? 0 : 100;
            return streamRank + SourceKindRank(source.Info.SourceKind);
        }

        private static int SourceKindRank(MediaFrameSourceKind sourceKind)
        {
            return sourceKind switch
            {
                MediaFrameSourceKind.Color => 0,
                MediaFrameSourceKind.Custom => 1,
                _ => 10
            };
        }

        private static string CreateMissingPlaybackStreamMessage(MediaCapture mediaCapture)
        {
            if (mediaCapture.FrameSources.Count == 0)
            {
                return "The selected device did not expose any Media Foundation frame sources after initialization.";
            }

            string sources = string.Join(
                "\n",
                mediaCapture.FrameSources.Values.Select(source =>
                    $"{source.Info.Id}: {source.Info.MediaStreamType}, {source.Info.SourceKind}"));

            return $"No usable video stream was exposed by the selected device.\n\nAvailable streams:\n{sources}";
        }

        private static MediaFrameFormat? FindMatchingFrameFormat(MediaFrameSource frameSource, VideoModeOption mode)
        {
            return frameSource.SupportedFormats.FirstOrDefault(format =>
                format.VideoFormat is not null &&
                format.VideoFormat.Width == mode.Width &&
                format.VideoFormat.Height == mode.Height &&
                Math.Abs(GetFrameRate(format) - mode.FrameRate) < 0.01 &&
                string.Equals(NormalizePixelFormat(format.Subtype), NormalizePixelFormat(mode.PixelFormat), StringComparison.OrdinalIgnoreCase));
        }

        private static double GetFrameRate(MediaFrameFormat format)
        {
            return format.FrameRate.Denominator == 0
                ? 0
                : format.FrameRate.Numerator / (double)format.FrameRate.Denominator;
        }

        private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                await StopPlaybackAsync();
                await ShowModalAsync(
                    "Capture Card Error",
                    new TextBlock
                    {
                        Text = $"Preview playback failed.\n\n{args.ErrorMessage}",
                        TextWrapping = TextWrapping.Wrap
                    });
            });
        }

        private async void StopPlaybackButton_Click(object sender, RoutedEventArgs e)
        {
            await StopPlaybackAsync();
        }

        private async Task StopPlaybackAsync()
        {
            MediaPlayer? player = _mediaPlayer;
            MediaCapture? capture = _mediaCapture;
            _mediaPlayer = null;
            _mediaCapture = null;
            _previewFrameSource = null;

            if (player is not null)
            {
                player.MediaFailed -= MediaPlayer_MediaFailed;
                player.Pause();
                CapturePreviewElement.SetMediaPlayer(null);
                player.Dispose();
            }

            capture?.Dispose();

            _isPlaybackActive = false;
            _controlsHideTimer.Stop();
            ControlsLayer.Visibility = Visibility.Collapsed;
            PlaybackControlsBar.Visibility = Visibility.Visible;
            PlaybackViewer.Visibility = Visibility.Collapsed;
            StartupForm.Visibility = Visibility.Visible;
        }

        private void PlaybackViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isPlaybackActive)
            {
                ShowPlaybackControls(restartTimer: true);
            }
        }

        private void PlaybackControlsBar_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isControlsPointerOver = true;
            ShowPlaybackControls(restartTimer: false);
        }

        private void PlaybackControlsBar_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isControlsPointerOver = false;
            StartControlsHideTimer();
        }

        private void PlaybackControlsBar_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isDraggingControls)
            {
                Point pointerPosition = e.GetCurrentPoint(ControlsLayer).Position;
                MovePlaybackControls(pointerPosition.X - _dragPointerOffset.X, pointerPosition.Y - _dragPointerOffset.Y);
            }

            ShowPlaybackControls(restartTimer: !_isDraggingControls);
        }

        private void PlaybackControlsBar_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            PointerPoint pointer = e.GetCurrentPoint(PlaybackControlsBar);
            if (!pointer.Properties.IsLeftButtonPressed)
            {
                return;
            }

            _isDraggingControls = true;
            _dragPointerOffset = pointer.Position;
            PlaybackControlsBar.CapturePointer(e.Pointer);
            _controlsHideTimer.Stop();
        }

        private void PlaybackControlsBar_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isDraggingControls = false;
            PlaybackControlsBar.ReleasePointerCapture(e.Pointer);
            StartControlsHideTimer();
        }

        private void MovePlaybackControls(double left, double top)
        {
            double maxLeft = Math.Max(0, ControlsLayer.ActualWidth - PlaybackControlsBar.ActualWidth - 12);
            double maxTop = Math.Max(0, ControlsLayer.ActualHeight - PlaybackControlsBar.ActualHeight - 12);

            Canvas.SetLeft(PlaybackControlsBar, Math.Clamp(left, 12, maxLeft));
            Canvas.SetTop(PlaybackControlsBar, Math.Clamp(top, 12, maxTop));
        }

        private void ControlsLayer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isPlaybackActive && !_isDraggingControls)
            {
                PositionPlaybackControlsBottomCenter();
            }
        }

        private void PositionPlaybackControlsBottomCenter()
        {
            double barWidth = PlaybackControlsBar.ActualWidth;
            double barHeight = PlaybackControlsBar.ActualHeight;

            if (barWidth <= 0 || barHeight <= 0 || ControlsLayer.ActualWidth <= 0 || ControlsLayer.ActualHeight <= 0)
            {
                PlaybackControlsBar.Loaded += PlaybackControlsBar_Loaded;
                return;
            }

            Canvas.SetLeft(PlaybackControlsBar, Math.Max(12, (ControlsLayer.ActualWidth - barWidth) / 2));
            Canvas.SetTop(PlaybackControlsBar, Math.Max(12, ControlsLayer.ActualHeight - barHeight - 26));
        }

        private void PlaybackControlsBar_Loaded(object sender, RoutedEventArgs e)
        {
            PlaybackControlsBar.Loaded -= PlaybackControlsBar_Loaded;
            PositionPlaybackControlsBottomCenter();
        }

        private void ShowPlaybackControls(bool restartTimer)
        {
            PlaybackControlsBar.Visibility = Visibility.Visible;

            if (restartTimer)
            {
                StartControlsHideTimer();
            }
        }

        private void StartControlsHideTimer()
        {
            if (!_isPlaybackActive || _isControlsPointerOver || _isDraggingControls || _isSettingsDialogOpen)
            {
                return;
            }

            _controlsHideTimer.Stop();
            _controlsHideTimer.Start();
        }

        private void ControlsHideTimer_Tick(object? sender, object e)
        {
            _controlsHideTimer.Stop();

            if (_isPlaybackActive && !_isControlsPointerOver && !_isDraggingControls && !_isSettingsDialogOpen)
            {
                PlaybackControlsBar.Visibility = Visibility.Collapsed;
            }
        }

        private void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isMuted)
            {
                _isMuted = false;
                VolumeSlider.Value = Math.Max(1, _previousVolume);
            }
            else
            {
                _previousVolume = VolumeSlider.Value > 0 ? VolumeSlider.Value : _previousVolume;
                _isMuted = true;
                VolumeSlider.Value = 0;
            }

            UpdateVolumeIcon();
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (VolumeButtonIcon is null)
            {
                return;
            }

            if (e.NewValue > 0)
            {
                _previousVolume = e.NewValue;
                _isMuted = false;
            }
            else
            {
                _isMuted = true;
            }

            UpdateVolumeIcon();
        }

        private void UpdateVolumeIcon()
        {
            VolumeButtonIcon.Glyph = _isMuted ? "\uE74F" : "\uE767";
        }

        private async void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingDevices || DeviceComboBox.SelectedIndex < 0 || DeviceComboBox.SelectedIndex >= _captureDevices.Count)
            {
                return;
            }

            await SelectDeviceAsync(_captureDevices[DeviceComboBox.SelectedIndex]);
        }

        private async void VideoModeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleMenuFlyoutItem selectedItem || selectedItem.Tag is not VideoModeOption mode)
            {
                return;
            }

            UncheckVideoModeItems(ResolutionMenu.Items);
            selectedItem.IsChecked = true;
            await SelectVideoModeAsync(mode, saveSelection: true);
        }

        private static void UncheckVideoModeItems(IList<MenuFlyoutItemBase> items)
        {
            foreach (MenuFlyoutItemBase item in items)
            {
                if (item is ToggleMenuFlyoutItem toggleItem)
                {
                    toggleItem.IsChecked = false;
                }
                else if (item is MenuFlyoutSubItem subItem)
                {
                    UncheckVideoModeItems(subItem.Items);
                }
            }
        }

        private async void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            _isSettingsDialogOpen = true;
            _controlsHideTimer.Stop();
            ShowPlaybackControls(restartTimer: false);

            await ShowModalAsync("Settings", CreateSettingsContent());

            _isSettingsDialogOpen = false;
            StartControlsHideTimer();
        }

        private async void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowModalAsync(
                "Help",
                new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Consolation previews HDMI sources through USB capture hardware using the lowest-latency path available.",
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = "Connect the HDMI source, attach the capture card, choose the device and video mode, then start playback.",
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = "If the preview is blank later in development, try another USB port, a lower frame rate, or a different advertised pixel format.",
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                });
        }

        private async void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowModalAsync(
                "About Consolation",
                new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Consolation",
                            FontSize = 20,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                        },
                        new TextBlock
                        {
                            Text = "Open source low-latency HDMI preview for capture cards.",
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock { Text = GetBuildInfo(), TextWrapping = TextWrapping.Wrap },
                        new TextBlock
                        {
                            Text = "Uses Windows Media Foundation / UVC preview APIs for capture card detection and playback.",
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                });
        }

        private FrameworkElement CreateSettingsContent()
        {
            RadioButton statsOffRadio = new()
            {
                Content = "Off",
                GroupName = "VideoStatsPosition",
                IsChecked = _settings.VideoStatsPosition == "Off"
            };
            statsOffRadio.Checked += async (_, _) =>
            {
                _settings.VideoStatsPosition = "Off";
                await SaveSettingsAsync();
            };

            RadioButton statsBottomLeftRadio = new()
            {
                Content = "Bottom Left",
                GroupName = "VideoStatsPosition",
                IsChecked = _settings.VideoStatsPosition == "BottomLeft"
            };
            statsBottomLeftRadio.Checked += async (_, _) =>
            {
                _settings.VideoStatsPosition = "BottomLeft";
                await SaveSettingsAsync();
            };

            RadioButton statsBottomRightRadio = new()
            {
                Content = "Bottom Right",
                GroupName = "VideoStatsPosition",
                IsChecked = _settings.VideoStatsPosition == "BottomRight"
            };
            statsBottomRightRadio.Checked += async (_, _) =>
            {
                _settings.VideoStatsPosition = "BottomRight";
                await SaveSettingsAsync();
            };

            ToggleSwitch lowFpsSwitch = new()
            {
                Header = "Show Low FPS Warnings",
                IsOn = _settings.ShowLowFpsWarnings
            };
            lowFpsSwitch.Toggled += async (_, _) =>
            {
                _settings.ShowLowFpsWarnings = lowFpsSwitch.IsOn;
                await SaveSettingsAsync();
            };

            ToggleSwitch advancedStatsSwitch = new()
            {
                Header = "Show Advanced Video Stats",
                IsOn = _settings.ShowAdvancedVideoStats
            };
            advancedStatsSwitch.Toggled += async (_, _) =>
            {
                _settings.ShowAdvancedVideoStats = advancedStatsSwitch.IsOn;
                await SaveSettingsAsync();
            };

            RadioButton rotate0Radio = CreateRotationRadioButton("0 degrees", 0);
            RadioButton rotate90Radio = CreateRotationRadioButton("90 degrees", 90);
            RadioButton rotate180Radio = CreateRotationRadioButton("180 degrees", 180);
            RadioButton rotate270Radio = CreateRotationRadioButton("270 degrees", 270);

            ToggleSwitch flipHorizontalSwitch = new()
            {
                Header = "Horizontal",
                IsOn = _settings.FlipHorizontal
            };
            flipHorizontalSwitch.Toggled += async (_, _) =>
            {
                _settings.FlipHorizontal = flipHorizontalSwitch.IsOn;
                await SaveSettingsAsync();
            };

            ToggleSwitch flipVerticalSwitch = new()
            {
                Header = "Vertical",
                IsOn = _settings.FlipVertical
            };
            flipVerticalSwitch.Toggled += async (_, _) =>
            {
                _settings.FlipVertical = flipVerticalSwitch.IsOn;
                await SaveSettingsAsync();
            };

            StackPanel content = new()
            {
                Width = 680,
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Video Telemetry",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    },
                    new Grid
                    {
                        ColumnSpacing = 16,
                        ColumnDefinitions =
                        {
                            new ColumnDefinition { Width = new GridLength(150) },
                            new ColumnDefinition { Width = new GridLength(110) },
                            new ColumnDefinition { Width = new GridLength(160) },
                            new ColumnDefinition { Width = new GridLength(170) }
                        },
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "Show Video Stats",
                                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                VerticalAlignment = VerticalAlignment.Center
                            },
                            WithGridColumn(statsOffRadio, 1),
                            WithGridColumn(statsBottomLeftRadio, 2),
                            WithGridColumn(statsBottomRightRadio, 3)
                        }
                    },
                    new Grid
                    {
                        ColumnSpacing = 28,
                        ColumnDefinitions =
                        {
                            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                        },
                        Children =
                        {
                            lowFpsSwitch,
                            WithGridColumn(advancedStatsSwitch, 1)
                        }
                    },
                    new TextBlock
                    {
                        Text = "Video Transformation",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    },
                    new Grid
                    {
                        ColumnSpacing = 16,
                        ColumnDefinitions =
                        {
                            new ColumnDefinition { Width = new GridLength(76) },
                            new ColumnDefinition { Width = new GridLength(128) },
                            new ColumnDefinition { Width = new GridLength(128) },
                            new ColumnDefinition { Width = new GridLength(138) },
                            new ColumnDefinition { Width = new GridLength(138) }
                        },
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "Rotate",
                                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                VerticalAlignment = VerticalAlignment.Center
                            },
                            WithGridColumn(rotate0Radio, 1),
                            WithGridColumn(rotate90Radio, 2),
                            WithGridColumn(rotate180Radio, 3),
                            WithGridColumn(rotate270Radio, 4)
                        }
                    },
                    new Grid
                    {
                        ColumnSpacing = 28,
                        ColumnDefinitions =
                        {
                            new ColumnDefinition { Width = new GridLength(80) },
                            new ColumnDefinition { Width = new GridLength(180) },
                            new ColumnDefinition { Width = new GridLength(180) }
                        },
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "Flip",
                                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                VerticalAlignment = VerticalAlignment.Center
                            },
                            WithGridColumn(flipHorizontalSwitch, 1),
                            WithGridColumn(flipVerticalSwitch, 2)
                        }
                    }
                }
            };

            return content;
        }

        private static T WithGridColumn<T>(T element, int column) where T : FrameworkElement
        {
            Grid.SetColumn(element, column);
            return element;
        }

        private RadioButton CreateRotationRadioButton(string label, int degrees)
        {
            RadioButton radioButton = new()
            {
                Content = label,
                GroupName = "RotationDegrees",
                IsChecked = _settings.RotationDegrees == degrees
            };

            radioButton.Checked += async (_, _) =>
            {
                _settings.RotationDegrees = degrees;
                await SaveSettingsAsync();
            };

            return radioButton;
        }

        private Task ShowModalAsync(string title, FrameworkElement content)
        {
            _modalCompletionSource = new TaskCompletionSource();
            ModalTitleHost.Content = CreateDialogTitle(title);
            ModalContentHost.Content = content;
            ModalOverlay.Visibility = Visibility.Visible;
            return _modalCompletionSource.Task;
        }

        private void ModalCloseButton_Click(object sender, RoutedEventArgs e)
        {
            ModalOverlay.Visibility = Visibility.Collapsed;
            ModalTitleHost.Content = null;
            ModalContentHost.Content = null;
            _modalCompletionSource?.TrySetResult();
            _modalCompletionSource = null;
        }

        private static StackPanel CreateDialogTitle(string title)
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    new Image
                    {
                        Width = 42,
                        Height = 42,
                        Source = new BitmapImage(new Uri("ms-appx:///Assets/app-icon.png"))
                    },
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 26,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };
        }

        private static string GetBuildInfo()
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            string packageVersion = GetPackageVersion();
            string buildTime = File.GetLastWriteTime(Assembly.GetExecutingAssembly().Location).ToString("g");

            return $"Assembly version: {version}\nPackage version: {packageVersion}\nBuild time: {buildTime}";
        }

        private static string GetPackageVersion()
        {
            try
            {
                PackageVersion version = Package.Current.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch
            {
                return "unpackaged debug build";
            }
        }

        private static string NormalizePixelFormat(string pixelFormat)
        {
            return pixelFormat.ToUpperInvariant() switch
            {
                "MJPG" => "MJPEG",
                "YUYV" => "YUY2",
                _ => pixelFormat.ToUpperInvariant()
            };
        }

        private sealed record CaptureDeviceOption(string Id, string Name, IReadOnlyList<VideoModeOption> VideoModes);

        private sealed class VideoModeOption
        {
            public required VideoEncodingProperties Properties { get; init; }
            public required uint Width { get; init; }
            public required uint Height { get; init; }
            public required double FrameRate { get; init; }
            public required string PixelFormat { get; init; }
            public string ResolutionLabel => $"{Width} x {Height}";
            public string FrameRateLabel => $"{FrameRate:0.##} FPS";
            public string SettingsKey => $"{Width}x{Height}|{FrameRate:0.###}|{NormalizePixelFormat(PixelFormat)}";

            public static VideoModeOption FromProperties(VideoEncodingProperties properties)
            {
                return new VideoModeOption
                {
                    Properties = properties,
                    Width = properties.Width,
                    Height = properties.Height,
                    FrameRate = properties.FrameRate.Numerator / (double)properties.FrameRate.Denominator,
                    PixelFormat = NormalizePixelFormat(properties.Subtype)
                };
            }
        }

        private sealed class SavedVideoMode
        {
            public uint Width { get; set; }
            public uint Height { get; set; }
            public double FrameRate { get; set; }
            public string PixelFormat { get; set; } = string.Empty;

            public static SavedVideoMode FromMode(VideoModeOption mode)
            {
                return new SavedVideoMode
                {
                    Width = mode.Width,
                    Height = mode.Height,
                    FrameRate = mode.FrameRate,
                    PixelFormat = NormalizePixelFormat(mode.PixelFormat)
                };
            }
        }

        private sealed class AppSettings
        {
            public string VideoStatsPosition { get; set; } = "BottomLeft";
            public bool ShowLowFpsWarnings { get; set; } = true;
            public bool ShowAdvancedVideoStats { get; set; }
            public int RotationDegrees { get; set; }
            public bool FlipHorizontal { get; set; }
            public bool FlipVertical { get; set; }
            public Dictionary<string, SavedVideoMode> DeviceVideoModes { get; set; } = [];
        }
    }
}
