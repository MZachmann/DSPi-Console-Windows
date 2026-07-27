using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using DSPiConsole.Controls;
using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace DSPiConsole;

public sealed partial class TestSignalsWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>Row shown in the type grid.</summary>
    private sealed class TypeItem
    {
        public SiggenTypeDesc Desc { get; init; } = null!;
        public string Name { get; init; } = "";
        public Microsoft.UI.Xaml.Media.Geometry Waveform { get; init; } = null!;
    }

    private readonly MainViewModel _viewModel;
    private readonly SiggenConfig _config;
    private bool _isUpdating = true;
    private bool _running;
    private MaskChipGrid? _channelGrid;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly DispatcherQueueTimer _applyTimer;

    public TestSignalsWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        _config = viewModel.SiggenConfig;

        InitializeComponent();

        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        double dpiScale = GetDpiForWindow(hWnd) / 96.0;
        appWindow?.Resize(new Windows.Graphics.SizeInt32((int)(460 * dpiScale), (int)(720 * dpiScale)));
        appWindow!.Title = "Test Signal Generator";

        if (appWindow.TitleBar is { } titleBar)
        {
            titleBar.ForegroundColor = Windows.UI.Color.FromArgb(255, 220, 220, 220);
            titleBar.BackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 140, 140, 140);
            titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 220, 220, 220);
            titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 140, 140, 140);
            titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 50, 50, 50);
        }

        _pollTimer = DispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(600);
        _pollTimer.Tick += (_, _) => _ = _viewModel.PollSiggenStatusAsync();

        // SET_CONFIG while running makes the firmware fade out, swap and fade back in,
        // so coalesce edits (slider drags, spinner clicks) into one restart.
        _applyTimer = DispatcherQueue.CreateTimer();
        _applyTimer.Interval = TimeSpan.FromMilliseconds(200);
        _applyTimer.IsRepeating = false;
        _applyTimer.Tick += (_, _) =>
        {
            if (_running && _config.ChannelMask != 0) _ = _viewModel.ApplySiggenConfigAsync();
        };

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed;

        BuildUi();
    }

    private void BuildUi()
    {
        if (!_viewModel.SiggenSupported)
        {
            UnsupportedBar.IsOpen = true;
            BodyPanel.Visibility = Visibility.Collapsed;
            TransportBar.Visibility = Visibility.Collapsed;
            return;
        }

        _isUpdating = true;

        // Type grid
        var items = new List<TypeItem>();
        foreach (var d in _viewModel.SiggenTypeDescs)
            items.Add(new TypeItem { Desc = d, Name = FriendlyName(d), Waveform = SiggenIcons.Get(d.Id) });
        TypeGrid.ItemsSource = items;

        // Level
        LevelSlider.Value = _config.LevelDb;
        LevelBox.Text = _config.LevelDb.ToString("F0", CultureInfo.InvariantCulture);

        // Flags
        RawToggle.IsChecked = _config.Flags.HasFlag(SiggenFlags.Raw);
        DecorrToggle.IsChecked = _config.Flags.HasFlag(SiggenFlags.Decorrelate);
        WalkToggle.IsChecked = _config.Flags.HasFlag(SiggenFlags.Walk);

        BuildChannelMask();

        // Select the config's current type (falls back to first).
        int sel = 0;
        for (int i = 0; i < items.Count; i++)
            if (items[i].Desc.Id == _config.SignalType) { sel = i; break; }
        TypeGrid.SelectedIndex = items.Count > 0 ? sel : -1;

        if (items.Count > 0)
        {
            // Keep the draft on the type the grid actually highlights.
            _config.SignalType = items[sel].Desc.Id;
            ApplyTypeConstraints(items[sel].Desc);
        }

        _isUpdating = false;

        if (items.Count > 0)
            BuildTypeSpecific(items[sel].Desc);

        // ItemsPanelRoot may not exist until the grid is realized; retry on load.
        LayoutTypeGrid();
        TypeGrid.Loaded += (_, _) => LayoutTypeGrid();

        UpdateStatus(_viewModel.SiggenStatus);
    }

    // The firmware reports terse lowercase short names (e.g. "sine", "swp_log");
    // always show a proper display name keyed on the type instead.
    // Divide the grid's width into equal cells so the cards fill the row evenly
    // rather than leaving a ragged gap on the right.
    private void OnTypeGridSizeChanged(object sender, SizeChangedEventArgs e) => LayoutTypeGrid();

    private void LayoutTypeGrid()
    {
        if (TypeGrid.ItemsPanelRoot is not ItemsWrapGrid wg) return;
        double w = TypeGrid.ActualWidth;
        if (w <= 0) return;
        const int cols = 3;
        wg.ItemWidth = Math.Floor(w / cols);
        wg.ItemHeight = 74;
    }

    private static string FriendlyName(SiggenTypeDesc d) => d.Id switch
    {
        SiggenType.Sine => "Sine",
        SiggenType.Square => "Square",
        SiggenType.White => "White Noise",
        SiggenType.Pink => "Pink Noise",
        SiggenType.SweepLog => "Log Sweep",
        SiggenType.SweepLin => "Linear Sweep",
        SiggenType.SweepStep => "Stepped Sweep",
        SiggenType.Impulse => "Impulse",
        SiggenType.ClicksAlt => "Alternating Clicks",
        SiggenType.Polarity => "Polarity Test",
        SiggenType.ToneBurst => "Tone Burst",
        SiggenType.TonePair => "Tone Pair",
        SiggenType.Multitone => "Multitone",
        SiggenType.Isp => "Inter-Sample Peak",
        SiggenType.ChannelId => "Channel ID",
        _ => d.Id.ToString()
    };

    // ── Channel mask ──

    private void BuildChannelMask()
    {
        int count = _viewModel.SiggenCaps?.OutputChannels ?? _viewModel.NumOutputChannels;
        if (count <= 0) count = _viewModel.NumOutputChannels;
        var outputs = _viewModel.ActiveOutputs;

        _channelGrid = new MaskChipGrid(
            MaskChipGrid.AllBits(count),
            bit => (bit < outputs.Count ? outputs[bit].Name : $"Output {bit + 1}")
                   + " — right-click to invert polarity",
            OnChannelToggle,
            stretch: true,
            captionForBit: bit => bit < outputs.Count && outputs[bit].ShortName == "PDM" ? "S" : null,
            onSecondaryToggle: OnChannelInvertToggle);

        ChannelHost.Children.Clear();
        ChannelHost.Children.Add(_channelGrid.Root);
        _channelGrid.SetMask(_config.ChannelMask);
        _channelGrid.SetSecondaryMask(_config.InvertMask);

        int all = _viewModel.SiggenCaps?.UsableChannelMask
                  ?? (count >= 16 ? 0xFFFF : (1 << count) - 1);
        var flyout = new MenuFlyout();
        void AddPreset(string text, int mask)
        {
            var mi = new MenuFlyoutItem { Text = text };
            mi.Click += (_, _) => SetChannelMask((ushort)mask);
            flyout.Items.Add(mi);
        }
        AddPreset("All outputs", all);
        AddPreset("First pair only", 0x0003);
        AddPreset("None", 0x0000);
        ChannelPresets.Flyout = flyout;
    }

    private void OnChannelToggle(int index, bool on)
    {
        int mask = _config.ChannelMask;
        if (on) mask |= (1 << index); else mask &= ~(1 << index);
        SetChannelMask((ushort)mask);
    }

    /// <summary>Right-click on a selected channel flips its polarity for the generated
    /// signal (SiggenConfig.invert_mask, which the firmware clamps to channel_mask).</summary>
    private void OnChannelInvertToggle(int index)
    {
        _config.InvertMask ^= (ushort)(1 << index);
        _config.InvertMask &= _config.ChannelMask;
        _channelGrid?.SetSecondaryMask(_config.InvertMask);
        ApplyIfRunning();
    }

    private void SetChannelMask(ushort mask)
    {
        _config.ChannelMask = mask;
        _config.InvertMask &= mask;   // deselecting a channel drops its invert bit
        _channelGrid?.SetMask(mask);
        _channelGrid?.SetSecondaryMask(_config.InvertMask);

        if (mask == 0)
        {
            // The firmware rejects an empty channel mask, so there is nothing to send.
            // Stop rather than leaving the generator running on channels the UI has
            // already deselected.
            _applyTimer.Stop();
            if (_running) { _ = _viewModel.StopSiggenAsync(); return; }
        }

        if (_running) ApplyIfRunning();
        else UpdateStatus(_viewModel.SiggenStatus);   // refresh the empty-mask hint
    }

    // ── Per-type parameters + timing ──

    /// <summary>Flags the firmware ignores (or forces) for this type, reflected in the
    /// checkboxes and cleared from the draft so the wire config matches what is shown.</summary>
    private void ApplyTypeConstraints(SiggenTypeDesc desc)
    {
        bool wasUpdating = _isUpdating;
        _isUpdating = true;

        bool noise = desc.Id is SiggenType.White or SiggenType.Pink;
        DecorrToggle.IsEnabled = noise;
        ToolTipService.SetToolTip(DecorrToggle,
            noise ? null : "Applies to white and pink noise only.");
        if (!noise) _config.Flags &= ~SiggenFlags.Decorrelate;
        DecorrToggle.IsChecked = _config.Flags.HasFlag(SiggenFlags.Decorrelate);

        // Channel ID always walks, whatever the flag says.
        bool forcedWalk = desc.Id == SiggenType.ChannelId;
        WalkToggle.IsEnabled = !forcedWalk;
        ToolTipService.SetToolTip(WalkToggle,
            forcedWalk ? "Channel ID always walks the selected outputs." : null);
        if (forcedWalk) _config.Flags |= SiggenFlags.Walk;
        WalkToggle.IsChecked = _config.Flags.HasFlag(SiggenFlags.Walk);

        _isUpdating = wasUpdating;
    }

    private void BuildTypeSpecific(SiggenTypeDesc desc)
    {
        ParamHost.Children.Clear();
        TimingHost.Children.Clear();

        // Params and timing fields carry over when the type changes, and the firmware
        // silently substitutes its own fallbacks for zero/out-of-range values. Seed the
        // draft from the descriptor first so the rows show what will actually play.
        desc.SeedParams(_config);
        desc.SeedTiming(_config, _config.Flags);

        // Disambiguate repeated semantics ("Frequency 1", "Frequency 2").
        var semCounts = new Dictionary<SiggenParamSemantic, int>();
        foreach (var p in desc.Params)
            if (p.IsUsed) semCounts[p.Semantic] = semCounts.GetValueOrDefault(p.Semantic) + 1;
        var semSeen = new Dictionary<SiggenParamSemantic, int>();

        for (int i = 0; i < desc.Params.Length; i++)
        {
            var p = desc.Params[i];
            if (!p.IsUsed) continue;
            int idx = i;
            string label = SemanticLabel(p.Semantic);
            if (semCounts[p.Semantic] > 1)
            {
                int n = semSeen.GetValueOrDefault(p.Semantic) + 1;
                semSeen[p.Semantic] = n;
                label = $"{label} {n}";
            }
            AddNumberRow(ParamHost, label, SemanticUnit(p.Semantic),
                _config.GetParam(idx), p.Min, p.Max, ParamStep(p.Semantic),
                v => { _config.SetParam(idx, (float)v); ApplyIfRunning(); });
        }

        // Timing rows depend on the type's timing model — and, for continuous types,
        // on WALK: it turns duration_ms into a per-channel dwell and brings repeat
        // (passes over the mask) into play.
        switch (desc.TimingModel)
        {
            case SiggenTimingModel.Continuous when desc.WalksWith(_config.Flags):
                AddNumberRow(TimingHost, "Dwell per channel", "ms",
                    _config.DurationMs, 100, 600000, 100,
                    v => { _config.DurationMs = (uint)Math.Max(100, v); ApplyIfRunning(); });
                AddNumberRow(TimingHost, "Passes", "over the selected outputs (0 = until stopped)",
                    _config.Repeat, 0, 9999, 1,
                    v => { _config.Repeat = (ushort)Math.Max(0, v); ApplyIfRunning(); });
                break;
            case SiggenTimingModel.Continuous:
                AddNumberRow(TimingHost, "Duration", "ms (0 = until stopped)",
                    _config.DurationMs, 0, 600000, 100,
                    v => { _config.DurationMs = (uint)Math.Max(0, v); ApplyIfRunning(); });
                break;
            case SiggenTimingModel.Sweep:
                AddNumberRow(TimingHost, "Sweep time", "ms",
                    _config.DurationMs, 10, 600000, 100,
                    v => { _config.DurationMs = (uint)Math.Max(10, v); ApplyIfRunning(); });
                AddNumberRow(TimingHost, "Repeat", "sweeps (0 = until stopped)",
                    _config.Repeat, 0, 9999, 1,
                    v => { _config.Repeat = (ushort)Math.Max(0, v); ApplyIfRunning(); });
                AddNumberRow(TimingHost, "Gap", "ms of silence between sweeps",
                    _config.GapMs, 0, 60000, 10,
                    v => { _config.GapMs = (ushort)Math.Max(0, v); ApplyIfRunning(); });
                break;
            case SiggenTimingModel.Pattern:
                AddNumberRow(TimingHost, "Repeat", "periods (0 = until stopped)",
                    _config.Repeat, 0, 9999, 1,
                    v => { _config.Repeat = (ushort)Math.Max(0, v); ApplyIfRunning(); });
                AddNumberRow(TimingHost, "Gap", "ms of extra silence per period",
                    _config.GapMs, 0, 60000, 10,
                    v => { _config.GapMs = (ushort)Math.Max(0, v); ApplyIfRunning(); });
                break;
        }
    }

    private static string SemanticLabel(SiggenParamSemantic s) => s switch
    {
        SiggenParamSemantic.FreqHz => "Frequency",
        SiggenParamSemantic.Ms => "Time",
        SiggenParamSemantic.Cycles => "Cycles",
        SiggenParamSemantic.Count => "Count",
        SiggenParamSemantic.Ratio => "Ratio",
        SiggenParamSemantic.Pattern => "Pattern",
        _ => "Value"
    };

    private static string SemanticUnit(SiggenParamSemantic s) => s switch
    {
        SiggenParamSemantic.FreqHz => "Hz",
        SiggenParamSemantic.Ms => "ms",
        _ => ""
    };

    private static double ParamStep(SiggenParamSemantic s) => s switch
    {
        SiggenParamSemantic.FreqHz => 10,
        SiggenParamSemantic.Ms => 10,
        SiggenParamSemantic.Ratio => 0.1,
        _ => 1
    };

    private void AddNumberRow(Panel host, string label, string unit, double value,
                              double min, double max, double step, Action<double> onChanged)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label });
        if (!string.IsNullOrEmpty(unit))
            text.Children.Add(new TextBlock
            {
                Text = unit,
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var box = new NumberBox
        {
            Value = Math.Clamp(value, min, max),
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 130,
            VerticalAlignment = VerticalAlignment.Center
        };
        box.ValueChanged += (_, e) =>
        {
            if (_isUpdating || double.IsNaN(e.NewValue)) return;
            onChanged(e.NewValue);
        };
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        host.Children.Add(grid);

        // The box shows a clamped value; push it back into the draft so the config
        // cannot keep an out-of-range number the firmware would silently replace with
        // a fallback of its own.
        if (box.Value != value) onChanged(box.Value);
    }

    // ── Type selection ──

    private void OnTypeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating || TypeGrid.SelectedItem is not TypeItem item) return;
        _config.SignalType = item.Desc.Id;
        ApplyTypeConstraints(item.Desc);
        BuildTypeSpecific(item.Desc);
        ApplyIfRunning();
    }

    private SiggenTypeDesc? SelectedDesc =>
        (TypeGrid.SelectedItem as TypeItem)?.Desc;

    // ── Level ──

    private void OnLevelChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        _config.LevelDb = (float)e.NewValue;
        LevelBox.Text = e.NewValue.ToString("F0", CultureInfo.InvariantCulture);
        _isUpdating = false;
        ApplyIfRunning();
    }

    private void OnLevelCommit(object sender, RoutedEventArgs e) => CommitLevelText();

    private void OnLevelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        CommitLevelText();
        e.Handled = true;
    }

    private void CommitLevelText()
    {
        if (_isUpdating) return;
        if (!float.TryParse(LevelBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
        {
            LevelBox.Text = _config.LevelDb.ToString("F0", CultureInfo.InvariantCulture);
            return;
        }

        v = Math.Clamp(v, SiggenConfig.LevelMinDb, SiggenConfig.LevelMaxDb);
        _isUpdating = true;
        _config.LevelDb = v;
        LevelSlider.Value = v;
        LevelBox.Text = v.ToString("F0", CultureInfo.InvariantCulture);
        _isUpdating = false;
        ApplyIfRunning();
    }

    // ── Flags ──

    private void OnFlagChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        var flags = SiggenFlags.None;
        if (RawToggle.IsChecked == true) flags |= SiggenFlags.Raw;
        if (DecorrToggle.IsChecked == true) flags |= SiggenFlags.Decorrelate;
        if (WalkToggle.IsChecked == true) flags |= SiggenFlags.Walk;
        _config.Flags = flags;

        // WALK reinterprets the timing fields of a continuous signal, so the timing
        // rows have to be rebuilt when it changes.
        if (SelectedDesc is { } desc) BuildTypeSpecific(desc);

        ApplyIfRunning();
    }

    // ── Transport ──

    private async void OnStartStopClick(object sender, RoutedEventArgs e)
    {
        if (!_running && _config.ChannelMask == 0)
        {
            // SET_CONFIG rejects an empty mask; say so instead of showing "Rejected".
            StatusText.Text = "Idle";
            StatusDetail.Text = "Select at least one output channel.";
            return;
        }

        // Drop any debounced edit: SET_CONFIG landing during the stop's fade-out is a
        // restart to the firmware, and Start sends the whole draft anyway.
        _applyTimer.Stop();

        StartStopButton.IsEnabled = false;
        try
        {
            if (_running)
            {
                await _viewModel.StopSiggenAsync();
            }
            else
            {
                bool ok = await _viewModel.StartSiggenAsync();
                if (!ok)
                {
                    StatusText.Text = "Rejected";
                    StatusDetail.Text = "The device declined the signal configuration.";
                }
            }
        }
        finally
        {
            StartStopButton.IsEnabled = true;
        }
    }

    /// <summary>Restage the draft on a running generator, debounced: every SET_CONFIG
    /// while running costs a fade-out/swap/fade-in restart in the firmware.</summary>
    private void ApplyIfRunning()
    {
        if (!_running) return;
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    // ── Status ──

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.SiggenStatus):
                    UpdateStatus(_viewModel.SiggenStatus);
                    break;
                case nameof(MainViewModel.SiggenSupported):
                    BuildUi();
                    break;
            }
        });
    }

    private void UpdateStatus(SiggenStatus? status)
    {
        bool running = status?.IsRunning ?? false;
        _running = running;
        StartStopButton.Content = running ? "Stop" : "Start";

        if (running) _pollTimer.Start(); else _pollTimer.Stop();

        if (status == null || !running)
        {
            StatusText.Text = "Idle";
            StatusDetail.Text = _config.ChannelMask == 0
                ? "Select at least one output channel." : "";
            return;
        }

        StatusText.Text = status.State switch
        {
            SiggenState.FadeIn => "Starting…",
            SiggenState.Run => "Running",
            SiggenState.Gap => "Gap",
            SiggenState.FadeOut => "Stopping…",
            _ => "Running"
        };

        var parts = new List<string>();
        if (status.CurrentFreq > 0)
            parts.Add($"{status.CurrentFreq:F0} Hz");
        if (status.ActiveChannel != 0xFF)
            parts.Add($"ch {status.ActiveChannel + 1}");
        if (status.CyclesDone > 0)
            parts.Add($"{status.CyclesDone} cycles");
        parts.Add($"{status.ElapsedMs / 1000.0:F1} s");
        StatusDetail.Text = string.Join("  ·  ", parts);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _pollTimer.Stop();
        _applyTimer.Stop();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        // Closing the window is the user's only stop affordance, so never leave a signal
        // playing with nothing to control it. Sent unconditionally rather than on
        // _running: a start could still be in flight, and CONTROL(STOP) on an idle
        // generator is a no-op the firmware acknowledges. The fade avoids a click.
        if (_viewModel.SiggenSupported) _ = _viewModel.StopSiggenAsync();
    }
}
