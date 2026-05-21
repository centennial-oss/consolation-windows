using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
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
using Windows.Foundation;
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

        private readonly DispatcherTimer _simulatedConnectionTimer = new()
        {
            Interval = TimeSpan.FromSeconds(1.4)
        };

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true
        };

        private AppSettings _settings = new();
        private bool _isPlaybackActive;
        private bool _isControlsPointerOver;
        private bool _isDraggingControls;
        private bool _isSettingsDialogOpen;
        private bool _isMuted;
        private Point _dragPointerOffset;
        private double _previousVolume = 70;
        private TaskCompletionSource? _modalCompletionSource;
        private string _selectedResolution = "1920 x 1080";
        private string _selectedFrameRate = "60 FPS";
        private string _selectedPixelFormat = "NV12";

        public MainWindow()
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            MaximizeWindow();
            _controlsHideTimer.Tick += ControlsHideTimer_Tick;
            _simulatedConnectionTimer.Tick += SimulatedConnectionTimer_Tick;
            _ = LoadSettingsAsync();
        }

        private async Task LoadSettingsAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(SettingsFileName);
                string json = await FileIO.ReadTextAsync(file);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                _settings.VideoStatsPosition ??= "BottomLeft";
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

        private void MaximizeWindow()
        {
            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
            if (AppWindow.GetFromWindowId(windowId)?.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            _isPlaybackActive = true;
            StartupForm.Visibility = Visibility.Collapsed;
            PlaybackViewer.Visibility = Visibility.Visible;
            ConnectingOverlay.Visibility = Visibility.Visible;
            SimulatedVideoFrame.Visibility = Visibility.Collapsed;
            ControlsLayer.Visibility = Visibility.Visible;
            PositionPlaybackControlsBottomCenter();
            ShowPlaybackControls(restartTimer: true);
            _simulatedConnectionTimer.Stop();
            _simulatedConnectionTimer.Start();
        }

        private void StopPlaybackButton_Click(object sender, RoutedEventArgs e)
        {
            _isPlaybackActive = false;
            _simulatedConnectionTimer.Stop();
            _controlsHideTimer.Stop();
            ControlsLayer.Visibility = Visibility.Collapsed;
            PlaybackControlsBar.Visibility = Visibility.Visible;
            PlaybackViewer.Visibility = Visibility.Collapsed;
            StartupForm.Visibility = Visibility.Visible;
        }

        private void SimulatedConnectionTimer_Tick(object? sender, object e)
        {
            _simulatedConnectionTimer.Stop();

            if (_isPlaybackActive)
            {
                ConnectingOverlay.Visibility = Visibility.Collapsed;
                SimulatedVideoFrame.Visibility = Visibility.Visible;
            }
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

        private void VideoModeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleMenuFlyoutItem selectedItem || selectedItem.Tag is not string tag)
            {
                return;
            }

            UncheckVideoModeItems(ResolutionMenu.Items);
            selectedItem.IsChecked = true;

            string[] modeParts = tag.Split('|');
            if (modeParts.Length == 3)
            {
                _selectedResolution = modeParts[0];
                _selectedFrameRate = modeParts[1];
                _selectedPixelFormat = modeParts[2];
                ResolutionMenu.Title = $"{_selectedResolution.Replace(" x ", "x")} @ {_selectedFrameRate.Replace(" FPS", "p")}";
                SelectedModeText.Text = $"Selected: {_selectedResolution}, {_selectedFrameRate}, {_selectedPixelFormat}";
            }
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
                            Text = "Windows UX prototype. UVC capture support will be wired in a later pass.",
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

        private sealed class AppSettings
        {
            public string VideoStatsPosition { get; set; } = "BottomLeft";
            public bool ShowLowFpsWarnings { get; set; } = true;
            public bool ShowAdvancedVideoStats { get; set; }
            public int RotationDegrees { get; set; }
            public bool FlipHorizontal { get; set; }
            public bool FlipVertical { get; set; }
        }
    }
}
