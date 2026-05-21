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
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Graphics;
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
        private bool _isPlaybackActive;
        private bool _isControlsPointerOver;
        private bool _isDraggingControls;
        private bool _isSettingsDialogOpen;
        private Point _dragPointerOffset;
        private string _selectedResolution = "1920 x 1080";
        private string _selectedFrameRate = "60 FPS";
        private string _selectedPixelFormat = "NV12";

        public MainWindow()
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            ResizeWindow(1180, 760);
            _controlsHideTimer.Tick += ControlsHideTimer_Tick;
            _ = LoadSettingsAsync();
        }

        private async Task LoadSettingsAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(SettingsFileName);
                string json = await FileIO.ReadTextAsync(file);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
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

        private void ResizeWindow(int width, int height)
        {
            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
            AppWindow.GetFromWindowId(windowId)?.Resize(new SizeInt32(width, height));
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            _isPlaybackActive = true;
            StartupForm.Visibility = Visibility.Collapsed;
            PlaybackViewer.Visibility = Visibility.Visible;
            ControlsLayer.Visibility = Visibility.Visible;
            ShowPlaybackControls(restartTimer: true);
        }

        private void StopPlaybackButton_Click(object sender, RoutedEventArgs e)
        {
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

            ContentDialog dialog = CreateSettingsDialog();
            await dialog.ShowAsync();

            _isSettingsDialogOpen = false;
            StartControlsHideTimer();
        }

        private async void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            ContentDialog dialog = CreateSimpleDialog(
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

            await dialog.ShowAsync();
        }

        private async void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            ContentDialog dialog = CreateSimpleDialog(
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

            await dialog.ShowAsync();
        }

        private ContentDialog CreateSettingsDialog()
        {
            ToggleSwitch statsOverlaySwitch = new()
            {
                Header = "Video stats overlay",
                IsOn = _settings.ShowVideoStatsOverlay
            };
            statsOverlaySwitch.Toggled += async (_, _) =>
            {
                _settings.ShowVideoStatsOverlay = statsOverlaySwitch.IsOn;
                await SaveSettingsAsync();
            };

            ToggleSwitch lowFpsSwitch = new()
            {
                Header = "Low-FPS warnings",
                IsOn = _settings.ShowLowFpsWarnings
            };
            lowFpsSwitch.Toggled += async (_, _) =>
            {
                _settings.ShowLowFpsWarnings = lowFpsSwitch.IsOn;
                await SaveSettingsAsync();
            };

            ComboBox rotateComboBox = new()
            {
                Header = "Rotate",
                Width = 240,
                SelectedValuePath = "Tag"
            };
            rotateComboBox.Items.Add(new ComboBoxItem { Content = "0 degrees", Tag = 0 });
            rotateComboBox.Items.Add(new ComboBoxItem { Content = "90 degrees", Tag = 90 });
            rotateComboBox.Items.Add(new ComboBoxItem { Content = "180 degrees", Tag = 180 });
            rotateComboBox.Items.Add(new ComboBoxItem { Content = "270 degrees", Tag = 270 });
            rotateComboBox.SelectedValue = _settings.RotationDegrees;
            rotateComboBox.SelectionChanged += async (_, _) =>
            {
                if (rotateComboBox.SelectedValue is int degrees)
                {
                    _settings.RotationDegrees = degrees;
                    await SaveSettingsAsync();
                }
            };

            CheckBox flipHorizontalCheckBox = new()
            {
                Content = "Flip horizontal",
                IsChecked = _settings.FlipHorizontal
            };
            flipHorizontalCheckBox.Checked += async (_, _) =>
            {
                _settings.FlipHorizontal = true;
                await SaveSettingsAsync();
            };
            flipHorizontalCheckBox.Unchecked += async (_, _) =>
            {
                _settings.FlipHorizontal = false;
                await SaveSettingsAsync();
            };

            CheckBox flipVerticalCheckBox = new()
            {
                Content = "Flip vertical",
                IsChecked = _settings.FlipVertical
            };
            flipVerticalCheckBox.Checked += async (_, _) =>
            {
                _settings.FlipVertical = true;
                await SaveSettingsAsync();
            };
            flipVerticalCheckBox.Unchecked += async (_, _) =>
            {
                _settings.FlipVertical = false;
                await SaveSettingsAsync();
            };

            StackPanel content = new()
            {
                Spacing = 16,
                Children =
                {
                    statsOverlaySwitch,
                    lowFpsSwitch,
                    new TextBlock
                    {
                        Text = "Video transformations",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    },
                    rotateComboBox,
                    flipHorizontalCheckBox,
                    flipVerticalCheckBox
                }
            };

            return CreateSimpleDialog("Settings", content);
        }

        private ContentDialog CreateSimpleDialog(string title, object content)
        {
            return new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = title,
                Content = content,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close
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
            public bool ShowVideoStatsOverlay { get; set; } = true;
            public bool ShowLowFpsWarnings { get; set; } = true;
            public int RotationDegrees { get; set; }
            public bool FlipHorizontal { get; set; }
            public bool FlipVertical { get; set; }
        }
    }
}
