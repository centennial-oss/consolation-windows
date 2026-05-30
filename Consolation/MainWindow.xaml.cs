using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
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
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Media.Render;
using Windows.Storage;
using Windows.System;

namespace Consolation
{
    public sealed partial class MainWindow : Window
    {
        private const string SettingsFileName = "settings.json";
        private const string DialogIconUri = "ms-appx:///Assets/Square44x44Logo.scale-200.png";
        private const double ModalContentWidth = 680;
        private static readonly string[] DeviceContainerProperties = ["System.Devices.ContainerId"];
        private static readonly HashSet<(uint Width, uint Height)> WellKnownResolutions = new()
        {
            (3840, 2160),
            (2560, 1440),
            (1920, 1080),
            (1280, 720)
        };

        private static readonly IReadOnlyDictionary<string, string> PixelFormatAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ARGB32"] = "ARGB32",
            ["BGRA8"] = "BGRA8",
            ["I420"] = "I420",
            ["IYUV"] = "IYUV",
            ["MJPEG"] = "MJPEG",
            ["MJPG"] = "MJPEG",
            ["NV12"] = "NV12",
            ["P010"] = "P010",
            ["P016"] = "P016",
            ["P210"] = "P210",
            ["P216"] = "P216",
            ["RGB1"] = "RGB1",
            ["RGB4"] = "RGB4",
            ["RGB8"] = "RGB8",
            ["RGB24"] = "RGB24",
            ["RGB32"] = "RGB32",
            ["RGB555"] = "RGB555",
            ["RGB565"] = "RGB565",
            ["UYVY"] = "UYVY",
            ["Y210"] = "Y210",
            ["Y216"] = "Y216",
            ["Y410"] = "Y410",
            ["Y416"] = "Y416",
            ["Y41P"] = "Y41P",
            ["YUY2"] = "YUY2",
            ["YUYV"] = "YUY2",
            ["YV12"] = "YV12",
            ["YVU9"] = "YVU9",
            ["YVYU"] = "YVYU"
        };

        private readonly DispatcherTimer _controlsHideTimer = new()
        {
            Interval = TimeSpan.FromSeconds(3)
        };

        private readonly DispatcherTimer _statsTimer = new()
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        private readonly SemaphoreSlim _deviceRefreshLock = new(1, 1);
        private readonly SemaphoreSlim _settingsSaveLock = new(1, 1);
        private AppSettings _settings = new();
        private DeviceWatcher? _deviceWatcher;
        private MediaCapture? _mediaCapture;
        private MediaFrameSource? _previewFrameSource;
        private MediaFrameFormat? _activeVideoFormat;
        private MediaFrameReader? _statsFrameReader;
        private MediaPlayer? _mediaPlayer;
        private AudioGraph? _audioGraph;
        private AudioDeviceInputNode? _audioInputNode;
        private AudioDeviceOutputNode? _audioOutputNode;
        private DeviceAccessInformation? _cameraAccessInformation;
        private DeviceAccessInformation? _microphoneAccessInformation;
        private readonly List<CaptureDeviceOption> _captureDevices = [];
        private CaptureDeviceOption? _selectedDevice;
        private VideoModeOption? _selectedVideoMode;
        private bool _isPlaybackActive;
        private bool _isLoadingDevices;
        private bool _isControlsPointerOver;
        private bool _isDraggingControls;
        private bool _isPanningVideo;
        private bool _isSettingsDialogOpen;
        private bool _isMuted;
        private bool _screenSaverSuppressed;
        private Point _dragPointerOffset;
        private Point _panStartPointer;
        private Point _panStartOffset;
        private Point _videoPanOffset;
        private double _previousVolume = 70;
        private double _zoomPercent;
        private long _framesSinceLastStats;
        private int _lowFpsSeconds;
        private double _actualFrameRate;
        private TaskCompletionSource? _modalCompletionSource;

        public MainWindow()
        {
            InitializeComponent();

            SetWindowIcon();
            ExtendsContentIntoTitleBar = true;
            MaximizeWindow();
            InitializePermissionNotice();
            _controlsHideTimer.Tick += ControlsHideTimer_Tick;
            _statsTimer.Tick += StatsTimer_Tick;
            _ = InitializeCaptureDevicesAsync();
        }

        private async Task LoadSettingsAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(SettingsFileName);
                string json = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrWhiteSpace(json))
                {
                    await SaveSettingsAsync();
                    return;
                }

                _settings = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings) ?? new AppSettings();
                _settings.VideoStatsPosition ??= "Off";
                _settings.DeviceVideoModes ??= [];
            }
            catch (FileNotFoundException)
            {
                await SaveSettingsAsync();
            }
            catch
            {
                _settings = new AppSettings();
                await SaveSettingsAsync();
            }
        }

        private async Task SaveSettingsAsync()
        {
            await _settingsSaveLock.WaitAsync();

            try
            {
                StorageFolder folder = ApplicationData.Current.LocalFolder;
                StorageFile tempFile = await folder.CreateFileAsync($"{SettingsFileName}.tmp", CreationCollisionOption.ReplaceExisting);
                string json = JsonSerializer.Serialize(_settings, AppJsonSerializerContext.Default.AppSettings);

                await FileIO.WriteTextAsync(tempFile, json);

                try
                {
                    StorageFile settingsFile = await folder.GetFileAsync(SettingsFileName);
                    await tempFile.MoveAndReplaceAsync(settingsFile);
                }
                catch (FileNotFoundException)
                {
                    await tempFile.RenameAsync(SettingsFileName, NameCollisionOption.ReplaceExisting);
                }
            }
            finally
            {
                _settingsSaveLock.Release();
            }
        }

        private void InitializePermissionNotice()
        {
            _cameraAccessInformation = DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.VideoCapture);
            _microphoneAccessInformation = DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.AudioCapture);

            _cameraAccessInformation.AccessChanged += DeviceAccessInformation_AccessChanged;
            _microphoneAccessInformation.AccessChanged += DeviceAccessInformation_AccessChanged;

            UpdatePermissionNotice();
        }

        private void DeviceAccessInformation_AccessChanged(DeviceAccessInformation sender, DeviceAccessChangedEventArgs args)
        {
            _ = DispatcherQueue.TryEnqueue(UpdatePermissionNotice);
        }

        private void UpdatePermissionNotice()
        {
            bool cameraAllowed = _cameraAccessInformation?.CurrentStatus == DeviceAccessStatus.Allowed;
            bool microphoneAllowed = _microphoneAccessInformation?.CurrentStatus == DeviceAccessStatus.Allowed;

            PermissionNoticePanel.Visibility = cameraAllowed && microphoneAllowed
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private async Task InitializeCaptureDevicesAsync()
        {
            await LoadSettingsAsync();
            await RefreshCaptureDevicesAsync();
            StartDeviceWatcher();
        }

        private async Task RefreshCaptureDevicesAsync()
        {
            await _deviceRefreshLock.WaitAsync();
            _isLoadingDevices = true;

            try
            {
                string? previouslySelectedDeviceId = _selectedDevice?.SettingsKey ?? _selectedDevice?.Id;

                DeviceComboBox.Items.Clear();
                _captureDevices.Clear();
                _selectedDevice = null;
                _selectedVideoMode = null;
                ClearVideoModeMenuItems();

                DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(MediaDevice.GetVideoCaptureSelector(), DeviceContainerProperties);
                List<CaptureDeviceOption> discoveredDevices = [];
                foreach (DeviceInformation device in devices.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    CaptureDeviceOption? option = await TryCreateCaptureDeviceOptionAsync(device);
                    if (option is null || option.VideoModes.Count == 0)
                    {
                        continue;
                    }

                    discoveredDevices.Add(option);
                }

                foreach (CaptureDeviceOption option in DedupeCaptureDevices(discoveredDevices))
                {
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
                    PlayButton.IsEnabled = false;
                    return;
                }

                DeviceComboBox.IsEnabled = true;
                int preferredDeviceIndex = GetPreferredDeviceIndex(previouslySelectedDeviceId);

                DeviceComboBox.SelectedIndex = preferredDeviceIndex;
                await SelectDeviceAsync(_captureDevices[preferredDeviceIndex]);
            }
            finally
            {
                _isLoadingDevices = false;
                _deviceRefreshLock.Release();
            }
        }

        private int GetPreferredDeviceIndex(string? previouslySelectedDeviceId)
        {
            if (previouslySelectedDeviceId is not null)
            {
                int existingDeviceIndex = _captureDevices.FindIndex(device => device.Id == previouslySelectedDeviceId || device.SettingsKey == previouslySelectedDeviceId);
                if (existingDeviceIndex >= 0 && _captureDevices[existingDeviceIndex].DeviceSelectionScore >= GetHighestDeviceSelectionScore())
                {
                    return existingDeviceIndex;
                }
            }

            int highestScore = GetHighestDeviceSelectionScore();
            return _captureDevices.FindIndex(device => device.DeviceSelectionScore == highestScore);
        }

        private int GetHighestDeviceSelectionScore()
        {
            return _captureDevices.Max(device => device.DeviceSelectionScore);
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
                    .Where(mode => mode is not null)
                    .Select(mode => mode!)
                    .GroupBy(mode => mode.SettingsKey, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderBy(mode => FormatRank(mode.PixelFormat)).First())
                    .OrderBy(mode => ResolutionCohortRank(mode.Width, mode.Height))
                    .ThenByDescending(mode => mode.Width)
                    .ThenByDescending(mode => mode.Height)
                    .ThenByDescending(mode => mode.FrameRate)
                    .ThenBy(mode => FormatRank(mode.PixelFormat))
                    .ToList();

                string? audioDeviceId = await FindPairedAudioDeviceIdAsync(device);
                int deviceSelectionScore = ScoreCaptureCardLikelihood(device, audioDeviceId is not null, videoModes);

                return new CaptureDeviceOption(device.Id, GetDeviceSettingsKey(device), device.Name, audioDeviceId, deviceSelectionScore, videoModes);
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

        private static async Task<string?> FindPairedAudioDeviceIdAsync(DeviceInformation videoDevice)
        {
            Guid? videoContainerId = TryGetContainerId(videoDevice);
            if (videoContainerId is null)
            {
                return null;
            }

            DeviceInformationCollection audioDevices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector(), DeviceContainerProperties);
            foreach (DeviceInformation audioDevice in audioDevices)
            {
                if (TryGetContainerId(audioDevice) == videoContainerId)
                {
                    return audioDevice.Id;
                }
            }

            return null;
        }

        private static IReadOnlyList<CaptureDeviceOption> DedupeCaptureDevices(IEnumerable<CaptureDeviceOption> devices)
        {
            return devices
                .GroupBy(device => device.SettingsKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(device => device.DeviceSelectionScore)
                    .ThenByDescending(device => device.VideoModes.Count)
                    .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                    .First())
                .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static string GetDeviceSettingsKey(DeviceInformation device)
        {
            Guid? containerId = TryGetContainerId(device);
            return containerId is not null
                ? $"container:{containerId:D}"
                : $"id:{device.Id}";
        }

        private static Guid? TryGetContainerId(DeviceInformation device)
        {
            return device.Properties.TryGetValue("System.Devices.ContainerId", out object? containerId) && containerId is Guid guid
                ? guid
                : null;
        }

        private static int ScoreCaptureCardLikelihood(
            DeviceInformation device,
            bool hasPairedAudioEndpoint,
            IReadOnlyList<VideoModeOption> videoModes)
        {
            int score = 0;
            string searchableName = device.Name.ToUpperInvariant();

            if (hasPairedAudioEndpoint)
            {
                score += 35;
            }

            if (device.EnclosureLocation?.Panel is Windows.Devices.Enumeration.Panel.Front or Windows.Devices.Enumeration.Panel.Back)
            {
                score -= 70;
            }
            else
            {
                score += 8;
            }

            if (ContainsAny(searchableName, "CAPTURE", "HDMI", "GAME", "AVERMEDIA", "ELGATO", "CAM LINK", "MAGEWELL", "BLUEAVS", "GENKI", "EVGA", "RAZER RIPSAW", "PENGO", "MIRABOX"))
            {
                score += 60;
            }

            if (ContainsAny(searchableName, "UVC", "USB VIDEO", "VIDEO DEVICE"))
            {
                score += 20;
            }

            if (ContainsAny(searchableName, "WEBCAM", "WEB CAM", "CAMERA", "INTEGRATED", "FRONT", "REAR", "FACETIME", "REAL SENSE", "REALSENSE", "IR CAMERA"))
            {
                score -= 60;
            }

            if (videoModes.Any(mode => mode.Width >= 1920 && mode.FrameRate >= 59))
            {
                score += 12;
            }

            if (videoModes.Any(mode => mode.Width >= 2560 || mode.FrameRate >= 100))
            {
                score += 16;
            }

            if (videoModes.Any(mode => NormalizePixelFormat(mode.PixelFormat) is "MJPEG" or "YUY2" or "NV12"))
            {
                score += 6;
            }

            return score;
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
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
            if (!_settings.DeviceVideoModes.TryGetValue(device.SettingsKey, out SavedVideoMode? savedMode) &&
                !_settings.DeviceVideoModes.TryGetValue(device.Id, out savedMode))
            {
                return null;
            }

            return device.VideoModes.FirstOrDefault(mode =>
                mode.Width == savedMode.Width &&
                mode.Height == savedMode.Height &&
                Math.Abs(mode.FrameRate - savedMode.FrameRate) < 0.01 &&
                string.Equals(mode.PixelFormat, NormalizePixelFormat(savedMode.PixelFormat), StringComparison.OrdinalIgnoreCase));
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
                "NV12" => 0,
                "YUY2" or "YUYV" => 1,
                "UYVY" => 2,
                "I420" or "IYUV" or "YV12" => 3,
                "RGB24" or "RGB32" or "ARGB32" or "BGRA8" => 4,
                "MJPEG" or "MJPG" => 20,
                _ => 10
            };
        }

        private void BuildVideoModeMenu(IReadOnlyList<VideoModeOption> modes, VideoModeOption? selectedMode)
        {
            ClearVideoModeMenuItems();

            List<IGrouping<(uint Width, uint Height), VideoModeOption>> resolutionGroups = modes
                .GroupBy(mode => (mode.Width, mode.Height))
                .OrderBy(group => ResolutionCohortRank(group.Key.Width, group.Key.Height))
                .ThenByDescending(group => group.Key.Width)
                .ThenByDescending(group => group.Key.Height)
                .ToList();

            bool addedSeparator = false;
            foreach (IGrouping<(uint Width, uint Height), VideoModeOption> resolutionGroup in resolutionGroups)
            {
                if (!addedSeparator && ResolutionCohortRank(resolutionGroup.Key.Width, resolutionGroup.Key.Height) > 0 && ResolutionMenu.Items.Count > 0)
                {
                    ResolutionMenu.Items.Add(new MenuFlyoutSeparator());
                    addedSeparator = true;
                }

                MenuFlyoutSubItem resolutionItem = new()
                {
                    Text = $"{resolutionGroup.Key.Width} x {resolutionGroup.Key.Height}"
                };

                foreach (IGrouping<string, VideoModeOption> frameRateGroup in resolutionGroup
                    .GroupBy(mode => mode.FrameRateLabel)
                    .OrderByDescending(group => group.Max(mode => mode.FrameRate)))
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

        private void ClearVideoModeMenuItems()
        {
            while (ResolutionMenu.Items.Count > 0)
            {
                ResolutionMenu.Items.RemoveAt(ResolutionMenu.Items.Count - 1);
            }
        }

        private static int ResolutionCohortRank(uint width, uint height)
        {
            return WellKnownResolutions.Contains((width, height)) ? 0 : 1;
        }

        private async Task SelectVideoModeAsync(VideoModeOption? mode, bool saveSelection)
        {
            _selectedVideoMode = mode;

            if (mode is null)
            {
                ResolutionMenu.Title = "No video modes";
                PlayButton.IsEnabled = false;
                return;
            }

            ResolutionMenu.Title = $"{mode.Width}x{mode.Height} @ {mode.FrameRateLabel.Replace(" FPS", "p")}, {NormalizePixelFormat(mode.PixelFormat)}";
            PlayButton.IsEnabled = _selectedDevice is not null;

            if (saveSelection && _selectedDevice is not null)
            {
                _settings.DeviceVideoModes[_selectedDevice.SettingsKey] = SavedVideoMode.FromMode(mode);
                _settings.DeviceVideoModes.Remove(_selectedDevice.Id);
                await SaveSettingsAsync();
            }
        }

        private void MaximizeWindow()
        {
            if (GetOverlappedPresenter() is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }

        private void SetWindowIcon()
        {
            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
            AppWindow.GetFromWindowId(windowId)?.SetIcon("Assets/app-icon.ico");
        }

        private OverlappedPresenter? GetOverlappedPresenter()
        {
            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
            return AppWindow.GetFromWindowId(windowId)?.Presenter as OverlappedPresenter;
        }

        private void ShowWindowChrome()
        {
            if (GetOverlappedPresenter() is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(true, true);
            }

            ExtendsContentIntoTitleBar = true;
        }

        private void HideWindowChrome()
        {
            if (GetOverlappedPresenter() is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
            }
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice is null || _selectedVideoMode is null)
            {
                return;
            }

            _isPlaybackActive = true;
            SuppressScreenSaver();
            StartupForm.Visibility = Visibility.Collapsed;
            PlaybackViewer.Visibility = Visibility.Visible;
            ConnectingOverlay.Visibility = Visibility.Visible;
            ControlsLayer.Visibility = Visibility.Visible;
            PositionPlaybackControlsBottomCenter();
            ShowPlaybackControls(restartTimer: true);
            ApplyVideoTransform();
            ApplyStatsOverlayPosition();
            ResetVideoStats();

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
                    _activeVideoFormat = selectedFormat;
                }
                else
                {
                    await _mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(
                        _previewFrameSource.Info.MediaStreamType,
                        _selectedVideoMode.Properties);
                    _activeVideoFormat = FindClosestFrameFormat(_previewFrameSource, _selectedVideoMode);
                }

                _mediaPlayer = new MediaPlayer
                {
                    AutoPlay = false,
                    RealTimePlayback = true,
                    Source = MediaSource.CreateFromMediaFrameSource(_previewFrameSource)
                };

                _mediaPlayer.CommandManager.IsEnabled = false;
                _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
                CapturePreviewElement.SetMediaPlayer(_mediaPlayer);
                _mediaPlayer.Play();
                await SyncStatsReaderStateAsync();
                await StartAudioAsync(_selectedDevice.AudioDeviceId);
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
                string.Equals(TryRecognizePixelFormat(format.Subtype), mode.PixelFormat, StringComparison.OrdinalIgnoreCase));
        }

        private static MediaFrameFormat? FindClosestFrameFormat(MediaFrameSource frameSource, VideoModeOption mode)
        {
            return frameSource.CurrentFormat ??
                frameSource.SupportedFormats
                    .Where(format => format.VideoFormat is not null)
                    .OrderBy(format => format.VideoFormat.Width == mode.Width && format.VideoFormat.Height == mode.Height ? 0 : 1)
                    .ThenBy(format => Math.Abs(GetFrameRate(format) - mode.FrameRate))
                    .ThenBy(format => FormatRank(NormalizePixelFormat(format.Subtype)))
                    .FirstOrDefault();
        }

        private static double GetFrameRate(MediaFrameFormat format)
        {
            return format.FrameRate.Denominator == 0
                ? 0
                : format.FrameRate.Numerator / (double)format.FrameRate.Denominator;
        }

        // The stats reader is a second consumer of the capture frame source, which adds
        // memory-bandwidth and per-frame overhead to the render path. Only run it when its
        // output (the stats overlay or the low-FPS warning) is actually in use.
        private bool IsStatsReaderNeeded()
        {
            return _settings.VideoStatsPosition != "Off" || _settings.ShowLowFpsWarnings;
        }

        private async Task SyncStatsReaderStateAsync()
        {
            if (!_isPlaybackActive)
            {
                return;
            }

            if (IsStatsReaderNeeded())
            {
                await StartStatsReaderAsync();
            }
            else
            {
                await StopStatsReaderAsync();
            }
        }

        private async Task StartStatsReaderAsync()
        {
            if (_mediaCapture is null || _previewFrameSource is null || _statsFrameReader is not null)
            {
                return;
            }

            _statsFrameReader = await _mediaCapture.CreateFrameReaderAsync(_previewFrameSource);
            _statsFrameReader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            _statsFrameReader.FrameArrived += StatsFrameReader_FrameArrived;
            await _statsFrameReader.StartAsync();
            _statsTimer.Start();
        }

        private async Task StopStatsReaderAsync()
        {
            MediaFrameReader? statsReader = _statsFrameReader;
            _statsFrameReader = null;
            _statsTimer.Stop();

            if (statsReader is not null)
            {
                statsReader.FrameArrived -= StatsFrameReader_FrameArrived;
                await statsReader.StopAsync();
                statsReader.Dispose();
            }
        }

        private void StatsFrameReader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            Interlocked.Increment(ref _framesSinceLastStats);
        }

        private async Task StartAudioAsync(string? audioDeviceId)
        {
            if (string.IsNullOrWhiteSpace(audioDeviceId))
            {
                return;
            }

            AudioGraphSettings graphSettings = new(AudioRenderCategory.Media)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency
            };

            CreateAudioGraphResult graphResult = await AudioGraph.CreateAsync(graphSettings);
            if (graphResult.Status != AudioGraphCreationStatus.Success)
            {
                return;
            }

            _audioGraph = graphResult.Graph;

            CreateAudioDeviceOutputNodeResult outputResult = await _audioGraph.CreateDeviceOutputNodeAsync();
            if (outputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                StopAudio();
                return;
            }

            DeviceInformation audioDevice = await DeviceInformation.CreateFromIdAsync(audioDeviceId);
            CreateAudioDeviceInputNodeResult inputResult = await _audioGraph.CreateDeviceInputNodeAsync(MediaCategory.Media, null, audioDevice);
            if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                StopAudio();
                return;
            }

            _audioOutputNode = outputResult.DeviceOutputNode;
            _audioInputNode = inputResult.DeviceInputNode;
            _audioInputNode.AddOutgoingConnection(_audioOutputNode, GetAudioGain());
            _audioGraph.Start();
        }

        private double GetAudioGain()
        {
            return _isMuted ? 0 : Math.Clamp(VolumeSlider.Value / 100, 0, 1);
        }

        private void ApplyAudioGain()
        {
            if (_audioInputNode is not null && _audioOutputNode is not null)
            {
                _audioInputNode.OutgoingGain = GetAudioGain();
            }
        }

        private void StopAudio()
        {
            _audioGraph?.Stop();
            _audioInputNode?.Dispose();
            _audioOutputNode?.Dispose();
            _audioGraph?.Dispose();
            _audioInputNode = null;
            _audioOutputNode = null;
            _audioGraph = null;
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
            await StopStatsReaderAsync();

            MediaPlayer? player = _mediaPlayer;
            MediaCapture? capture = _mediaCapture;
            _mediaPlayer = null;
            _mediaCapture = null;
            _previewFrameSource = null;
            _activeVideoFormat = null;
            LowFpsWarningButton.Visibility = Visibility.Collapsed;
            VideoStatsOverlay.Visibility = Visibility.Collapsed;
            ShowWindowChrome();
            RestoreScreenSaver();
            StopAudio();

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
                if (_isPanningVideo)
                {
                    Point pointerPosition = e.GetCurrentPoint(PlaybackViewer).Position;
                    _videoPanOffset = ClampVideoPanOffset(new Point(
                        _panStartOffset.X + pointerPosition.X - _panStartPointer.X,
                        _panStartOffset.Y + pointerPosition.Y - _panStartPointer.Y));
                    ApplyVideoTransform();
                    e.Handled = true;
                }

                ShowPlaybackControls(restartTimer: !_isPanningVideo);
            }
        }

        private void PlaybackViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            PointerPoint pointer = e.GetCurrentPoint(PlaybackViewer);
            if (!_isPlaybackActive || GetVideoZoomScale() <= 1 || !pointer.Properties.IsLeftButtonPressed)
            {
                return;
            }

            _isPanningVideo = true;
            _panStartPointer = pointer.Position;
            _panStartOffset = _videoPanOffset;
            PlaybackViewer.CapturePointer(e.Pointer);
            _controlsHideTimer.Stop();
            e.Handled = true;
        }

        private void PlaybackViewer_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isPanningVideo)
            {
                return;
            }

            _isPanningVideo = false;
            PlaybackViewer.ReleasePointerCapture(e.Pointer);
            StartControlsHideTimer();
            e.Handled = true;
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

        private void CapturePreviewElement_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _videoPanOffset = ClampVideoPanOffset(_videoPanOffset);
            ApplyVideoTransform();
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
            ShowWindowChrome();
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
                HideWindowChrome();
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
            ApplyAudioGain();
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
            ApplyAudioGain();
        }

        private void ZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            _zoomPercent = Math.Clamp(e.NewValue, 0, 100);
            _videoPanOffset = ClampVideoPanOffset(_videoPanOffset);
            ApplyVideoTransform();
        }

        private void UpdateVolumeIcon()
        {
            VolumeButtonIcon.Glyph = _isMuted ? "\uE74F" : "\uE767";
        }

        private void StatsTimer_Tick(object? sender, object e)
        {
            long frameCount = Interlocked.Exchange(ref _framesSinceLastStats, 0);
            _actualFrameRate = frameCount;

            UpdateVideoStatsOverlay();
            UpdateLowFpsWarning();
        }

        private void ResetVideoStats()
        {
            Interlocked.Exchange(ref _framesSinceLastStats, 0);
            _actualFrameRate = 0;
            _lowFpsSeconds = 0;
            LowFpsWarningButton.Visibility = Visibility.Collapsed;
            VideoStatsOverlay.Visibility = _settings.VideoStatsPosition == "Off" ? Visibility.Collapsed : Visibility.Visible;
            UpdateVideoStatsOverlay();
        }

        private void UpdateVideoStatsOverlay()
        {
            if (_settings.VideoStatsPosition == "Off")
            {
                VideoStatsOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            VideoStatsOverlay.Visibility = Visibility.Visible;

            string resolution = _activeVideoFormat?.VideoFormat is not null
                ? $"{_activeVideoFormat.VideoFormat.Width}x{_activeVideoFormat.VideoFormat.Height}"
                : _selectedVideoMode is not null ? $"{_selectedVideoMode.Width}x{_selectedVideoMode.Height}" : "Unknown";

            double reportedFrameRate = _activeVideoFormat is not null ? GetFrameRate(_activeVideoFormat) : _selectedVideoMode?.FrameRate ?? 0;
            string format = _activeVideoFormat is not null ? NormalizePixelFormat(_activeVideoFormat.Subtype) : _selectedVideoMode?.PixelFormat ?? "Unknown";
            string basic = $"{resolution}/{reportedFrameRate:0.##} | {format} | Fps:{_actualFrameRate:0}";

            VideoStatsText.Text = _settings.ShowAdvancedVideoStats
                ? $"{basic} | Req:{_selectedVideoMode?.FrameRate:0.##} | Stream:{_previewFrameSource?.Info.MediaStreamType} | Src:{_previewFrameSource?.Info.SourceKind}"
                : basic;
        }

        private void UpdateLowFpsWarning()
        {
            if (!_settings.ShowLowFpsWarnings || _selectedVideoMode is null)
            {
                LowFpsWarningButton.Visibility = Visibility.Collapsed;
                _lowFpsSeconds = 0;
                return;
            }

            if (_selectedVideoMode.FrameRate - _actualFrameRate >= 10)
            {
                _lowFpsSeconds++;
            }
            else
            {
                _lowFpsSeconds = 0;
            }

            LowFpsWarningButton.Visibility = _lowFpsSeconds >= 3 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyStatsOverlayPosition()
        {
            bool statsRight = _settings.VideoStatsPosition == "BottomRight";
            VideoStatsOverlay.HorizontalAlignment = statsRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            LowFpsWarningButton.HorizontalAlignment = statsRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            VideoStatsOverlay.Visibility = _settings.VideoStatsPosition == "Off" ? Visibility.Collapsed : VideoStatsOverlay.Visibility;
        }

        private void ApplyVideoTransform()
        {
            double zoomScale = GetVideoZoomScale();
            _videoPanOffset = ClampVideoPanOffset(_videoPanOffset);

            CapturePreviewTransform.CenterX = CapturePreviewElement.ActualWidth / 2;
            CapturePreviewTransform.CenterY = CapturePreviewElement.ActualHeight / 2;
            CapturePreviewTransform.Rotation = _settings.RotationDegrees;
            CapturePreviewTransform.ScaleX = (_settings.FlipHorizontal ? -1 : 1) * zoomScale;
            CapturePreviewTransform.ScaleY = (_settings.FlipVertical ? -1 : 1) * zoomScale;
            CapturePreviewTransform.TranslateX = _videoPanOffset.X;
            CapturePreviewTransform.TranslateY = _videoPanOffset.Y;
        }

        private double GetVideoZoomScale()
        {
            return 1 + Math.Clamp(_zoomPercent, 0, 200) / 100;
        }

        private Point ClampVideoPanOffset(Point offset)
        {
            double zoomScale = GetVideoZoomScale();
            if (zoomScale <= 1 || CapturePreviewElement.ActualWidth <= 0 || CapturePreviewElement.ActualHeight <= 0)
            {
                return new Point(0, 0);
            }

            double maxX = CapturePreviewElement.ActualWidth * (zoomScale - 1) / 2;
            double maxY = CapturePreviewElement.ActualHeight * (zoomScale - 1) / 2;

            return new Point(
                Math.Clamp(offset.X, -maxX, maxX),
                Math.Clamp(offset.Y, -maxY, maxY));
        }

        private void SuppressScreenSaver()
        {
            SetThreadExecutionState(
                ExecutionState.EsContinuous |
                ExecutionState.EsDisplayRequired |
                ExecutionState.EsSystemRequired);
            _screenSaverSuppressed = true;
        }

        private void RestoreScreenSaver()
        {
            if (!_screenSaverSuppressed)
            {
                return;
            }

            SetThreadExecutionState(ExecutionState.EsContinuous);
            _screenSaverSuppressed = false;
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

            await ShowCustomModalAsync(CreateDialogTitle("Settings"), CreateSettingsContent(), CreateSettingsActions());

            _isSettingsDialogOpen = false;
            StartControlsHideTimer();
        }

        private async void LowFpsWarningButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowModalAsync(
                "Low FPS",
                new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "The capture card is delivering fewer frames than the selected mode requests.",
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = "For the best results, connect the capture card directly to the PC, avoid USB hubs, use a high-quality cable, and try a lower resolution or frame rate if the warning continues.",
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                });
        }

        private async void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowModalAsync(
                "Consolation Help",
                CreateHelpContent());
        }

        private async void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowCustomModalAsync(CreateAboutTitle(), CreateAboutContent(), CreateAboutActions());
        }

        private static FrameworkElement CreateHelpContent()
        {
            return new StackPanel
            {
                Width = ModalContentWidth,
                Spacing = 24,
                Children =
                {
                    CreateDivider(),
                    CreateInfoRow("\uE768", "Getting Started", "Connect a USB capture device, select it from the list, and press the Play button."),
                    CreateInfoRow("\uE799", "Frame Rate", "For best results, select the same frame rate as that of the source input. If playback lags, avoid USB hubs and replace low-quality cables."),
                    CreateInfoRow("\uE9A6", "Video Controls", "Use the Settings sheet to rotate/mirror the feed and show video stats."),
                    CreateInfoRow("\uE995", "Audio Controls", "Use the playback controls to mute audio and set the volume as desired."),
                    CreateInfoRow("\uE88E", "Device Support", "While any USB Video Class (UVC) device should work with Consolation, video quality ultimately depends on the capture device hardware. Some devices may advertise resolutions and frame rates beyond their actual capabilities."),
                    CreateDivider()
                }
            };
        }

        private FrameworkElement CreateAboutContent()
        {
            return new StackPanel
            {
                Width = ModalContentWidth,
                Spacing = 22,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Consolation and the Consolation logo are trademarks of Centennial OSS Inc.\nAll rights reserved.",
                        Foreground = SecondaryTextBrush(),
                        TextWrapping = TextWrapping.Wrap
                    },
                    CreateDivider(),
                    CreateInfoRow("\uE714", "Consolation is a USB Capture Card utility for viewing gaming consoles, Raspberry Pis, and other HDMI devices on a Windows PC.", iconOnly: true),
                    CreateInfoRow("\uE7BA", "External USB Video Class (UVC) capture hardware is required.", iconOnly: true),
                    CreateInfoRow("\uEA18", "Consolation is 100% private. It does not collect analytics or snoop on your usage. Nothing ever leaves your device. Period.", iconOnly: true),
                    CreateInfoRow("\uEB51", "This software is completely free and open source for you to enjoy.", iconOnly: true),
                    CreateBuildInfoSection(),
                    CreateDivider()
                }
            };
        }

        private static IEnumerable<UIElement> CreateAboutActions()
        {
            return
            [
                CreateModalActionButton("GitHub", "https://github.com/centennial-oss/consolation-windows"),
                CreateModalActionButton("Privacy Policy", "https://centennialoss.org/privacy/")
            ];
        }

        private static FrameworkElement CreateAboutTitle()
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 14,
                Children =
                {
                    new Image
                    {
                        Width = 64,
                        Height = 64,
                        Source = new BitmapImage(new Uri(DialogIconUri))
                    },
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Spacing = 4,
                        Children =
                        {
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 10,
                                Children =
                                {
                                    new TextBlock
                                    {
                                        Text = "Consolation\u2122",
                                        FontSize = 30,
                                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                                    },
                                    new TextBlock
                                    {
                                        Text = $"v{BuildInfo.Version}",
                                        Margin = new Thickness(0, 9, 0, 0),
                                        FontSize = 15,
                                        Foreground = SecondaryTextBrush()
                                    }
                                }
                            },
                            new TextBlock
                            {
                                Text = "Copyright \u00A9 2026 Centennial OSS Inc.",
                                FontSize = 14,
                                Foreground = SecondaryTextBrush()
                            }
                        }
                    }
                }
            };
        }

        private FrameworkElement CreateBuildInfoSection()
        {
            Button copyButton = new()
            {
                Content = "Copy to Clipboard",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                MinWidth = 160,
                CornerRadius = new CornerRadius(20)
            };

            copyButton.Click += (_, _) =>
            {
                DataPackage package = new() { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(BuildInfo.CopyableBlob);
                Clipboard.SetContent(package);
                copyButton.Content = "Copied";
            };

            Grid buildInfoGrid = new()
            {
                Padding = new Thickness(14, 12, 14, 12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 0, 0, 0)),
                CornerRadius = new CornerRadius(8),
                ColumnSpacing = 14,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                Children =
                {
                    new TextBlock
                    {
                        Text = BuildInfo.CopyableBlob,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                        FontSize = 18,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    },
                    WithGridColumn(copyButton, 1)
                }
            };

            return new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    CreateIconTextRow("\uE8A5", "Build info (copy for support)", SecondaryTextBrush(), 16),
                    buildInfoGrid
                }
            };
        }

        private static Grid CreateInfoRow(string glyph, string title, string body)
        {
            Grid row = CreateInfoRowShell(glyph);
            row.Children.Add(WithColumnLocal(new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 18,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = body,
                        FontSize = 17,
                        Foreground = SecondaryTextBrush(),
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 24
                    }
                }
            }, 1));
            return row;
        }

        private static Grid CreateInfoRow(string glyph, string text, bool iconOnly)
        {
            Grid row = CreateInfoRowShell(glyph);
            row.Children.Add(WithColumnLocal(new TextBlock
            {
                Text = text,
                FontSize = 17,
                Foreground = SecondaryTextBrush(),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 26
            }, 1));
            return row;
        }

        private static Grid CreateInfoRowShell(string glyph)
        {
            return new Grid
            {
                ColumnSpacing = 14,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(34) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                },
                Children =
                {
                    new FontIcon
                    {
                        Glyph = glyph,
                        FontSize = 22,
                        Foreground = SecondaryTextBrush(),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top
                    }
                }
            };
        }

        private static StackPanel CreateLinkRow(string glyph, string text)
        {
            return CreateIconTextRow(glyph, text, new SolidColorBrush(Windows.UI.Color.FromArgb(255, 54, 148, 255)), 18);
        }

        private static StackPanel CreateIconTextRow(string glyph, string text, Brush foreground, double fontSize)
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    new FontIcon
                    {
                        Glyph = glyph,
                        FontSize = fontSize,
                        Foreground = foreground,
                        Width = 34
                    },
                    new TextBlock
                    {
                        Text = text,
                        FontSize = fontSize,
                        Foreground = foreground,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            };
        }

        private static Border CreateDivider()
        {
            return new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(45, 255, 255, 255))
            };
        }

        private static SolidColorBrush SecondaryTextBrush()
        {
            return new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255));
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
                ApplyStatsOverlayPosition();
                UpdateVideoStatsOverlay();
                await SyncStatsReaderStateAsync();
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
                ApplyStatsOverlayPosition();
                UpdateVideoStatsOverlay();
                await SyncStatsReaderStateAsync();
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
                ApplyStatsOverlayPosition();
                UpdateVideoStatsOverlay();
                await SyncStatsReaderStateAsync();
                await SaveSettingsAsync();
            };

            ToggleSwitch lowFpsSwitch = new()
            {
                OnContent = string.Empty,
                OffContent = string.Empty,
                IsOn = _settings.ShowLowFpsWarnings
            };
            lowFpsSwitch.Toggled += async (_, _) =>
            {
                _settings.ShowLowFpsWarnings = lowFpsSwitch.IsOn;
                UpdateLowFpsWarning();
                await SyncStatsReaderStateAsync();
                await SaveSettingsAsync();
            };

            ToggleSwitch advancedStatsSwitch = new()
            {
                OnContent = string.Empty,
                OffContent = string.Empty,
                IsOn = _settings.ShowAdvancedVideoStats
            };
            advancedStatsSwitch.Toggled += async (_, _) =>
            {
                _settings.ShowAdvancedVideoStats = advancedStatsSwitch.IsOn;
                UpdateVideoStatsOverlay();
                await SaveSettingsAsync();
            };

            RadioButton rotate0Radio = CreateRotationRadioButton("0 degrees", 0);
            RadioButton rotate90Radio = CreateRotationRadioButton("90 degrees", 90);
            RadioButton rotate180Radio = CreateRotationRadioButton("180 degrees", 180);
            RadioButton rotate270Radio = CreateRotationRadioButton("270 degrees", 270);

            ToggleSwitch flipHorizontalSwitch = new()
            {
                OnContent = string.Empty,
                OffContent = string.Empty,
                IsOn = _settings.FlipHorizontal
            };
            flipHorizontalSwitch.Toggled += async (_, _) =>
            {
                _settings.FlipHorizontal = flipHorizontalSwitch.IsOn;
                ApplyVideoTransform();
                await SaveSettingsAsync();
            };

            ToggleSwitch flipVerticalSwitch = new()
            {
                OnContent = string.Empty,
                OffContent = string.Empty,
                IsOn = _settings.FlipVertical
            };
            flipVerticalSwitch.Toggled += async (_, _) =>
            {
                _settings.FlipVertical = flipVerticalSwitch.IsOn;
                ApplyVideoTransform();
                await SaveSettingsAsync();
            };

            StackPanel content = new()
            {
                Width = ModalContentWidth,
                Spacing = 20,
                Children =
                {
                    CreateDivider(),
                    CreateSettingsSectionTitle("Video Telemetry"),
                    new Grid
                    {
                        ColumnSpacing = 10,
                        ColumnDefinitions =
                        {
                            new ColumnDefinition { Width = new GridLength(170) },
                            new ColumnDefinition { Width = new GridLength(82) },
                            new ColumnDefinition { Width = new GridLength(132) },
                            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                        },
                        Children =
                        {
                            CreateSettingsRowLabel("Show Video Stats"),
                            WithGridColumn(statsOffRadio, 1),
                            WithGridColumn(statsBottomLeftRadio, 2),
                            WithGridColumn(statsBottomRightRadio, 3)
                        }
                    },
                    new Grid
                    {
                        ColumnSpacing = 30,
                        ColumnDefinitions =
                        {
                            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                        },
                        Children =
                        {
                            CreateSettingsToggleRow("Show Low FPS Warnings", lowFpsSwitch),
                            WithGridColumn(CreateSettingsToggleRow("Show Advanced Video Stats", advancedStatsSwitch), 1)
                        }
                    },
                    CreateSettingsSectionTitle("Video Transformation"),
                    new Grid
                    {
                        ColumnSpacing = 10,
                        ColumnDefinitions =
                        {
                            new ColumnDefinition { Width = new GridLength(82) },
                            new ColumnDefinition { Width = new GridLength(110) },
                            new ColumnDefinition { Width = new GridLength(110) },
                            new ColumnDefinition { Width = new GridLength(120) },
                            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                        },
                        Children =
                        {
                            CreateSettingsRowLabel("Rotate"),
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
                            new ColumnDefinition { Width = new GridLength(82) },
                            new ColumnDefinition { Width = new GridLength(245) },
                            new ColumnDefinition { Width = new GridLength(245) }
                        },
                        Children =
                        {
                            CreateSettingsRowLabel("Flip"),
                            WithGridColumn(CreateSettingsToggleRow("Horizontal", flipHorizontalSwitch), 1),
                            WithGridColumn(CreateSettingsToggleRow("Vertical", flipVerticalSwitch), 2)
                        }
                    },
                    CreateDivider()
                }
            };

            return content;
        }

        private static TextBlock CreateSettingsSectionTitle(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
            };
        }

        private static TextBlock CreateSettingsRowLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
            };
        }

        private static StackPanel CreateSettingsToggleRow(string label, ToggleSwitch toggleSwitch)
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    CreateSettingsRowLabel(label),
                    toggleSwitch
                }
            };
        }

        private IEnumerable<UIElement> CreateSettingsActions()
        {
            return
            [
                CreateModalNavigationButton("Help", () => SetModalContent(CreateDialogTitle("Consolation Help"), CreateHelpContent())),
                CreateModalNavigationButton("About", () => SetModalContent(CreateAboutTitle(), CreateAboutContent(), CreateAboutActions()))
            ];
        }

        private static T WithGridColumn<T>(T element, int column) where T : FrameworkElement
        {
            Grid.SetColumn(element, column);
            return element;
        }

        private static T WithColumnLocal<T>(T element, int column) where T : FrameworkElement
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
                ApplyVideoTransform();
                await SaveSettingsAsync();
            };

            return radioButton;
        }

        private Task ShowModalAsync(string title, FrameworkElement content)
        {
            return ShowCustomModalAsync(CreateDialogTitle(title), content);
        }

        private Task ShowCustomModalAsync(FrameworkElement title, FrameworkElement content, IEnumerable<UIElement>? actions = null)
        {
            _modalCompletionSource = new TaskCompletionSource();
            SetModalContent(title, content, actions);
            ModalOverlay.Visibility = Visibility.Visible;
            return _modalCompletionSource.Task;
        }

        private void SetModalContent(FrameworkElement title, FrameworkElement content, IEnumerable<UIElement>? actions = null)
        {
            ModalTitleHost.Content = title;
            ModalContentHost.Content = content;
            ModalActionsHost.Children.Clear();

            if (actions is null)
            {
                return;
            }

            foreach (UIElement action in actions)
            {
                ModalActionsHost.Children.Add(action);
            }
        }

        private void ModalCloseButton_Click(object sender, RoutedEventArgs e)
        {
            ModalOverlay.Visibility = Visibility.Collapsed;
            ModalTitleHost.Content = null;
            ModalContentHost.Content = null;
            ModalActionsHost.Children.Clear();
            _modalCompletionSource?.TrySetResult();
            _modalCompletionSource = null;
        }

        private static Button CreateModalActionButton(string text, string uri)
        {
            Button button = CreateModalFooterButton(text);
            button.Click += async (_, _) => await Launcher.LaunchUriAsync(new Uri(uri));
            return button;
        }

        private static Button CreateModalNavigationButton(string text, Action action)
        {
            Button button = CreateModalFooterButton(text);
            button.Click += (_, _) => action();
            return button;
        }

        private static Button CreateModalFooterButton(string text)
        {
            return new Button
            {
                MinWidth = 104,
                Height = 44,
                Padding = new Thickness(22, 0, 22, 0),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 70, 70, 70)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                Content = text,
                CornerRadius = new CornerRadius(22),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
            };
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
                        Source = new BitmapImage(new Uri(DialogIconUri))
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
            return TryRecognizePixelFormat(pixelFormat) ?? pixelFormat.ToUpperInvariant();
        }

        private static string? TryRecognizePixelFormat(string pixelFormat)
        {
            string trimmed = pixelFormat.Trim();
            if (PixelFormatAliases.TryGetValue(trimmed, out string? alias))
            {
                return alias;
            }

            if (Guid.TryParse(trimmed.Trim('{', '}'), out Guid guid))
            {
                string guidText = guid.ToString("D").ToUpperInvariant();
                return guidText switch
                {
                    "E436EB78-524F-11CE-9F53-0020AF0BA770" => "RGB1",
                    "E436EB79-524F-11CE-9F53-0020AF0BA770" => "RGB4",
                    "E436EB7A-524F-11CE-9F53-0020AF0BA770" => "RGB8",
                    "E436EB7B-524F-11CE-9F53-0020AF0BA770" => "RGB565",
                    "E436EB7C-524F-11CE-9F53-0020AF0BA770" => "RGB555",
                    "E436EB7D-524F-11CE-9F53-0020AF0BA770" => "RGB24",
                    "E436EB7E-524F-11CE-9F53-0020AF0BA770" => "RGB32",
                    _ => TryGetFourCcSubtype(guidText)
                };
            }

            return null;
        }

        private static string? TryGetFourCcSubtype(string guidText)
        {
            const string fourCcGuidSuffix = "-0000-0010-8000-00AA00389B71";
            if (!guidText.EndsWith(fourCcGuidSuffix, StringComparison.Ordinal))
            {
                return null;
            }

            string firstPart = guidText[..8];
            char[] chars =
            [
                (char)Convert.ToByte(firstPart[6..8], 16),
                (char)Convert.ToByte(firstPart[4..6], 16),
                (char)Convert.ToByte(firstPart[2..4], 16),
                (char)Convert.ToByte(firstPart[0..2], 16)
            ];

            string fourCc = new(chars);
            return PixelFormatAliases.TryGetValue(fourCc, out string? alias) ? alias : null;
        }

        [DllImport("kernel32.dll")]
        private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

        [Flags]
        private enum ExecutionState : uint
        {
            EsSystemRequired = 0x00000001,
            EsDisplayRequired = 0x00000002,
            EsContinuous = 0x80000000
        }

        private sealed record CaptureDeviceOption(
            string Id,
            string SettingsKey,
            string Name,
            string? AudioDeviceId,
            int DeviceSelectionScore,
            IReadOnlyList<VideoModeOption> VideoModes);

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

            public static VideoModeOption? FromProperties(VideoEncodingProperties properties)
            {
                string? pixelFormat = TryRecognizePixelFormat(properties.Subtype);
                if (pixelFormat is null)
                {
                    return null;
                }

                return new VideoModeOption
                {
                    Properties = properties,
                    Width = properties.Width,
                    Height = properties.Height,
                    FrameRate = properties.FrameRate.Numerator / (double)properties.FrameRate.Denominator,
                    PixelFormat = pixelFormat
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
            public string VideoStatsPosition { get; set; } = "Off";
            public bool ShowLowFpsWarnings { get; set; } = true;
            public bool ShowAdvancedVideoStats { get; set; }
            public int RotationDegrees { get; set; }
            public bool FlipHorizontal { get; set; }
            public bool FlipVertical { get; set; }
            public Dictionary<string, SavedVideoMode> DeviceVideoModes { get; set; } = [];
        }

        [JsonSourceGenerationOptions(WriteIndented = true)]
        [JsonSerializable(typeof(AppSettings))]
        private sealed partial class AppJsonSerializerContext : JsonSerializerContext
        {
        }
    }
}
