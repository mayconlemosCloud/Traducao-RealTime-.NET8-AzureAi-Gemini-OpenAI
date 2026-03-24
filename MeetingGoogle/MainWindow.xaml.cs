using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using MeetingGoogle.ViewModels;

namespace MeetingGoogle;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private Storyboard? _pulseStoryboard;
    private Storyboard? _slideDownStoryboard;
    private Storyboard? _slideUpStoryboard;
    private Storyboard? _subtitleFadeInStoryboard;
    
    private System.Windows.Threading.DispatcherTimer? _autoScrollResumeTimer;
    private bool _userIsReadingHistory;
    private System.Windows.Controls.ScrollViewer? _historyScrollViewer;

    [DllImport("user32.dll")]
    public static extern uint SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    // Hotkeys remanescentes vazias
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT   = 0x0004;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // Cache das novas storyboards
        _slideDownStoryboard = (Storyboard)FindResource("SlideDownAnimation");
        _slideUpStoryboard = (Storyboard)FindResource("SlideUpAnimation");
        _subtitleFadeInStoryboard = (Storyboard)FindResource("SubtitleFadeIn");

        _autoScrollResumeTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _autoScrollResumeTimer.Tick += AutoScrollResumeTimer_Tick;

        // Auto-scroll history when new items are added
        // Assuming History exists in your ViewModel
        if (_vm.History != null)
        {
            _vm.History.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add ||
                    e.Action == NotifyCollectionChangedAction.Replace)
                {
                    if (_vm.History.Count > 0 && _vm.IsHistoryVisible && HistoryList.IsVisible)
                    {
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                        {
                            try
                            {
                                if (_userIsReadingHistory) return;
                                
                                if (_vm.History.Count > 0)
                                {
                                    if (_historyScrollViewer != null)
                                        _historyScrollViewer.ScrollToBottom();
                                    else
                                        HistoryList.ScrollIntoView(_vm.History[_vm.History.Count - 1]);
                                }
                            }
                            catch
                            {
                                // Safety net
                            }
                        }));
                    }
                }
            };
        }

        HistoryList.Loaded += HistoryList_Loaded;

        // Wire up analyzing animation
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void HistoryList_Loaded(object sender, RoutedEventArgs e)
    {
        _historyScrollViewer = FindVisualChild<System.Windows.Controls.ScrollViewer>(HistoryList);
        if (_historyScrollViewer != null)
        {
            _historyScrollViewer.ScrollChanged += HistoryScrollViewer_ScrollChanged;
        }
    }

    private void HistoryScrollViewer_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        // Ignore changes that are purely due to extent/viewport resizing (like adding items)
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0 && e.VerticalChange != 0)
        {
            if (_historyScrollViewer == null) return;
            
            // Check if user scrolled far from the bottom
            const double tolerance = 5.0;
            bool isAtBottom = _historyScrollViewer.VerticalOffset >= (_historyScrollViewer.ScrollableHeight - tolerance);

            if (!isAtBottom)
            {
                _userIsReadingHistory = true;
                _autoScrollResumeTimer?.Stop();
                _autoScrollResumeTimer?.Start();
            }
            else
            {
                // User scrolled back to the bottom manually, we can resume auto-scroll immediately
                _userIsReadingHistory = false;
                _autoScrollResumeTimer?.Stop();
            }
        }
    }

    private void AutoScrollResumeTimer_Tick(object? sender, EventArgs e)
    {
        _autoScrollResumeTimer?.Stop();
        _userIsReadingHistory = false;
        
        if (_historyScrollViewer != null)
        {
            _historyScrollViewer.ScrollToBottom();
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
                return typedChild;
            
            var result = FindVisualChild<T>(child);
            if (result != null)
                return result;
        }
        return null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSettingsVisible))
        {
            var sb = _vm.IsSettingsVisible ? _slideDownStoryboard : _slideUpStoryboard;
            sb?.Begin(SettingsPanel);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsHistoryVisible))
        {
            var sb = _vm.IsHistoryVisible ? _slideDownStoryboard : _slideUpStoryboard;
            sb?.Begin(HistoryPanel);
        }
        else if (e.PropertyName == nameof(MainViewModel.SubtitleText))
        {
            _subtitleFadeInStoryboard?.Begin(SubtitleBlock);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                SubtitleScrollViewer?.ScrollToBottom();
            }));
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
    }

    private async void ToggleConnect_Click(object sender, RoutedEventArgs e)
    {
        await _vm.ToggleConnectionAsync();
    }

    private void ToggleHistory_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleHistory();
    }

    private void ToggleMute_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleMute();
    }

    private void ToggleSettings_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleSettings();
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        _vm.RefreshDevices();
    }

    private void ToggleSpeak_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleSpeakConnection();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _vm.ClearHistory();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        this.WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _vm.Dispose();
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var helper = new WindowInteropHelper(this);
        HwndSource source = HwndSource.FromHwnd(helper.Handle);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}