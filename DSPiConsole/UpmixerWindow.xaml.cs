using System;
using System.Globalization;
using System.Runtime.InteropServices;
using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using WinRT.Interop;

namespace DSPiConsole;

/// <summary>
/// Stereo upmixer editor (firmware wire V26, RP2350). Enable + centre / surround
/// engine modes and parameters, each pushed live through the ViewModel
/// (REQ_UPMIX_SET_PARAM), plus a 10 Hz telemetry strip (REQ_UPMIX_GET_STATUS)
/// with a parked-reason banner. Mirrors the Psychoacoustic Bass window.
/// </summary>
public sealed partial class UpmixerWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _statusTimer;
    private bool _isUpdating = true;
    private bool _closed;

    public UpmixerWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        double dpiScale = GetDpiForWindow(hWnd) / 96.0;
        appWindow?.Resize(new Windows.Graphics.SizeInt32((int)(460 * dpiScale), (int)(860 * dpiScale)));
        if (appWindow != null) appWindow.Title = "Stereo Upmixer";

        if (appWindow?.TitleBar is { } titleBar)
        {
            titleBar.ForegroundColor = Color.FromArgb(255, 220, 220, 220);
            titleBar.BackgroundColor = Color.FromArgb(255, 32, 32, 32);
            titleBar.InactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
            titleBar.InactiveBackgroundColor = Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 220, 220, 220);
            titleBar.ButtonBackgroundColor = Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
            titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 255, 255, 255);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 50, 50, 50);
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 10 Hz telemetry poll while the window is open (spec recommends 5-20 Hz).
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _statusTimer.Tick += async (_, _) =>
        {
            if (_viewModel.UpmixSupported && _viewModel.IsDeviceConnected)
                await _viewModel.PollUpmixStatusAsync();
        };

        Closed += (_, _) =>
        {
            _closed = true;
            _statusTimer.Stop();
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        };

        BuildUi();
        _statusTimer.Start();
    }

    private void BuildUi()
    {
        if (!_viewModel.UpmixSupported)
        {
            UnsupportedBar.IsOpen = true;
            BodyPanel.Visibility = Visibility.Collapsed;
            EnableToggle.IsEnabled = false;
            return;
        }

        UnsupportedBar.IsOpen = false;
        BodyPanel.Visibility = Visibility.Visible;
        EnableToggle.IsEnabled = true;

        _isUpdating = true;
        EnableToggle.IsOn = _viewModel.UpmixEnabled;
        CenterModeCombo.SelectedIndex = Math.Clamp(_viewModel.UpmixCenterMode, 0, 1);
        SurroundModeCombo.SelectedIndex = Math.Clamp(_viewModel.UpmixSurroundMode, 0, 2);
        SetPair(StrengthSlider, StrengthBox, _viewModel.UpmixStrengthPct, "F0");
        SetPair(WidthSlider, WidthBox, _viewModel.UpmixCenterWidthPct, "F0");
        SetPair(PresenceSlider, PresenceBox, _viewModel.UpmixPresenceDb, "F1");
        SetPair(ThresholdSlider, ThresholdBox, _viewModel.UpmixThresholdPct, "F0");
        SetPair(AttackSlider, AttackBox, _viewModel.UpmixAttackMs, "F0");
        SetPair(ReleaseSlider, ReleaseBox, _viewModel.UpmixReleaseMs, "F0");
        SetPair(DetHpfSlider, DetHpfBox, _viewModel.UpmixDetectorHpfHz, "F0");
        SetPair(SurDelaySlider, SurDelayBox, _viewModel.UpmixSurroundDelayMs, "F1");
        SetPair(SurHpfSlider, SurHpfBox, _viewModel.UpmixSurroundHpfHz, "F0");
        SetPair(SurLpfSlider, SurLpfBox, _viewModel.UpmixSurroundLpfHz, "F0");
        SetPair(DecorrSlider, DecorrBox, _viewModel.UpmixDecorrPct, "F0");
        _isUpdating = false;

        UpdateModeEnables();
    }

    private static void SetPair(Slider slider, TextBox box, float value, string fmt)
    {
        slider.Value = value;
        box.Text = value.ToString(fmt, CultureInfo.InvariantCulture);
    }

    /// <summary>Per-mode visibility (spec section 4): the adaptive steering
    /// controls only apply in adaptive centre mode — strength/width/presence
    /// stay shown in both modes — and the surround conditioning only applies
    /// while the surround engine is on. Inapplicable controls are hidden.</summary>
    private void UpdateModeEnables()
    {
        AdaptiveCenterPanel.Visibility = _viewModel.UpmixCenterMode == 1
            ? Visibility.Visible : Visibility.Collapsed;
        SurroundPanel.Visibility = _viewModel.UpmixSurroundMode != 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Telemetry ────────────────────────────────────────────────────────────

    private void UpdateStatusUi()
    {
        var st = _viewModel.UpmixStatus;
        if (st == null) return;

        CorrBar.Value = (st.Correlation + 1f) * 50f;
        CorrText.Text = st.Correlation.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        CenterBar.Value = st.CenterGain * 100f;
        CenterText.Text = ((int)MathF.Round(st.CenterGain * 100f)).ToString(CultureInfo.InvariantCulture) + "%";
        LsBar.Value = st.LsGain * 100f;
        LsText.Text = ((int)MathF.Round(st.LsGain * 100f)).ToString(CultureInfo.InvariantCulture) + "%";
        RsBar.Value = st.RsGain * 100f;
        RsText.Text = ((int)MathF.Round(st.RsGain * 100f)).ToString(CultureInfo.InvariantCulture) + "%";

        // Parked banner: only when the user has it enabled but the firmware
        // can't run it (reason 1 = simply disabled, which the toggle shows).
        if (_viewModel.UpmixEnabled && !st.Active && st.ParkedReason >= 2)
        {
            ParkedBar.Message = st.ParkedReason == 2
                ? "The active input is not a plain stereo pair. The upmixer resumes when a 2-channel source is selected."
                : "The sample rate is above 48 kHz. The upmixer resumes at 44.1/48 kHz.";
            ParkedBar.IsOpen = true;
        }
        else
        {
            ParkedBar.IsOpen = false;
        }
    }

    // ── ViewModel sync ───────────────────────────────────────────────────────

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.UpmixSupported):
                    BuildUi();
                    return;
                case nameof(MainViewModel.UpmixStatus):
                    UpdateStatusUi();
                    return;
                case nameof(MainViewModel.UpmixEnabled):
                    _isUpdating = true; EnableToggle.IsOn = _viewModel.UpmixEnabled; _isUpdating = false;
                    break;
                case nameof(MainViewModel.UpmixCenterMode):
                    _isUpdating = true; CenterModeCombo.SelectedIndex = Math.Clamp(_viewModel.UpmixCenterMode, 0, 1); _isUpdating = false;
                    UpdateModeEnables();
                    break;
                case nameof(MainViewModel.UpmixSurroundMode):
                    _isUpdating = true; SurroundModeCombo.SelectedIndex = Math.Clamp(_viewModel.UpmixSurroundMode, 0, 2); _isUpdating = false;
                    UpdateModeEnables();
                    break;
                case nameof(MainViewModel.UpmixStrengthPct):
                    _isUpdating = true; SetPair(StrengthSlider, StrengthBox, _viewModel.UpmixStrengthPct, "F0"); _isUpdating = false;
                    break;
                case nameof(MainViewModel.UpmixCenterWidthPct):
                    _isUpdating = true; SetPair(WidthSlider, WidthBox, _viewModel.UpmixCenterWidthPct, "F0"); _isUpdating = false;
                    break;
                case nameof(MainViewModel.UpmixPresenceDb):
                    _isUpdating = true; SetPair(PresenceSlider, PresenceBox, _viewModel.UpmixPresenceDb, "F1"); _isUpdating = false;
                    break;
                case nameof(MainViewModel.UpmixThresholdPct):
                    _isUpdating = true; SetPair(ThresholdSlider, ThresholdBox, _viewModel.UpmixThresholdPct, "F0"); _isUpdating = false;
                    break;
                case nameof(MainViewModel.UpmixAttackMs):
                    _isUpdating = true; SetPair(AttackSlider, AttackBox, _viewModel.UpmixAttackMs, "F0"); _isUpdating = false;
                    break;
                case nameof(MainViewModel.UpmixReleaseMs):
                    _isUpdating = true; SetPair(ReleaseSlider, ReleaseBox, _viewModel.UpmixReleaseMs, "F0"); _isUpdating = false;
                    break;
                case nameof(MainViewModel.UpmixDetectorHpfHz):
                    _isUpdating = true; SetPair(DetHpfSlider, DetHpfBox, _viewModel.UpmixDetectorHpfHz, "F0"); _isUpdating = false;
                    break;
                case nameof(MainViewModel.UpmixSurroundDelayMs):
                    _isUpdating = true; SetPair(SurDelaySlider, SurDelayBox, _viewModel.UpmixSurroundDelayMs, "F1"); _isUpdating = false;
                    break;
                case nameof(MainViewModel.UpmixSurroundHpfHz):
                    _isUpdating = true; SetPair(SurHpfSlider, SurHpfBox, _viewModel.UpmixSurroundHpfHz, "F0"); _isUpdating = false;
                    break;
                case nameof(MainViewModel.UpmixSurroundLpfHz):
                    _isUpdating = true; SetPair(SurLpfSlider, SurLpfBox, _viewModel.UpmixSurroundLpfHz, "F0"); _isUpdating = false;
                    break;
                case nameof(MainViewModel.UpmixDecorrPct):
                    _isUpdating = true; SetPair(DecorrSlider, DecorrBox, _viewModel.UpmixDecorrPct, "F0"); _isUpdating = false;
                    break;
            }
        });
    }

    // ── Control handlers ─────────────────────────────────────────────────────

    private void OnEnableToggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        _viewModel.UpmixEnabled = EnableToggle.IsOn;
    }

    private void OnCenterModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating || CenterModeCombo.SelectedIndex < 0) return;
        _viewModel.UpmixCenterMode = CenterModeCombo.SelectedIndex;
        UpdateModeEnables();
    }

    private void OnSurroundModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating || SurroundModeCombo.SelectedIndex < 0) return;
        _viewModel.UpmixSurroundMode = SurroundModeCombo.SelectedIndex;
        UpdateModeEnables();
    }

    private void OnStrengthChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        SliderChanged((float)e.NewValue, StrengthBox, "F0", v => _viewModel.UpmixStrengthPct = v);
    private void OnStrengthText(object sender, TextChangedEventArgs e) =>
        TextChanged(StrengthBox, StrengthSlider, UpmixLimits.StrengthMinPct, UpmixLimits.StrengthMaxPct, v => _viewModel.UpmixStrengthPct = v);

    private void OnWidthChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        SliderChanged((float)e.NewValue, WidthBox, "F0", v => _viewModel.UpmixCenterWidthPct = v);
    private void OnWidthText(object sender, TextChangedEventArgs e) =>
        TextChanged(WidthBox, WidthSlider, UpmixLimits.WidthMinPct, UpmixLimits.WidthMaxPct, v => _viewModel.UpmixCenterWidthPct = v);

    private void OnPresenceChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        SliderChanged((float)e.NewValue, PresenceBox, "F1", v => _viewModel.UpmixPresenceDb = v);
    private void OnPresenceText(object sender, TextChangedEventArgs e) =>
        TextChanged(PresenceBox, PresenceSlider, UpmixLimits.PresenceMinDb, UpmixLimits.PresenceMaxDb, v => _viewModel.UpmixPresenceDb = v);

    private void OnThresholdChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        SliderChanged((float)e.NewValue, ThresholdBox, "F0", v => _viewModel.UpmixThresholdPct = v);
    private void OnThresholdText(object sender, TextChangedEventArgs e) =>
        TextChanged(ThresholdBox, ThresholdSlider, UpmixLimits.ThresholdMinPct, UpmixLimits.ThresholdMaxPct, v => _viewModel.UpmixThresholdPct = v);

    private void OnAttackChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        SliderChanged((float)e.NewValue, AttackBox, "F0", v => _viewModel.UpmixAttackMs = v);
    private void OnAttackText(object sender, TextChangedEventArgs e) =>
        TextChanged(AttackBox, AttackSlider, UpmixLimits.AttackMinMs, UpmixLimits.AttackMaxMs, v => _viewModel.UpmixAttackMs = v);

    private void OnReleaseChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        SliderChanged((float)e.NewValue, ReleaseBox, "F0", v => _viewModel.UpmixReleaseMs = v);
    private void OnReleaseText(object sender, TextChangedEventArgs e) =>
        TextChanged(ReleaseBox, ReleaseSlider, UpmixLimits.ReleaseMinMs, UpmixLimits.ReleaseMaxMs, v => _viewModel.UpmixReleaseMs = v);

    private void OnDetHpfChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        SliderChanged((float)e.NewValue, DetHpfBox, "F0", v => _viewModel.UpmixDetectorHpfHz = v);
    private void OnDetHpfText(object sender, TextChangedEventArgs e) =>
        TextChanged(DetHpfBox, DetHpfSlider, UpmixLimits.DetHpfMinHz, UpmixLimits.DetHpfMaxHz, v => _viewModel.UpmixDetectorHpfHz = v);

    private void OnSurDelayChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        SliderChanged((float)e.NewValue, SurDelayBox, "F1", v => _viewModel.UpmixSurroundDelayMs = v);
    private void OnSurDelayText(object sender, TextChangedEventArgs e) =>
        TextChanged(SurDelayBox, SurDelaySlider, UpmixLimits.SurDelayMinMs, UpmixLimits.SurDelayMaxMs, v => _viewModel.UpmixSurroundDelayMs = v);

    private void OnSurHpfChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        SliderChanged((float)e.NewValue, SurHpfBox, "F0", v => _viewModel.UpmixSurroundHpfHz = v);
    private void OnSurHpfText(object sender, TextChangedEventArgs e) =>
        TextChanged(SurHpfBox, SurHpfSlider, UpmixLimits.SurHpfMinHz, UpmixLimits.SurHpfMaxHz, v => _viewModel.UpmixSurroundHpfHz = v);

    private void OnSurLpfChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        SliderChanged((float)e.NewValue, SurLpfBox, "F0", v => _viewModel.UpmixSurroundLpfHz = v);
    private void OnSurLpfText(object sender, TextChangedEventArgs e) =>
        TextChanged(SurLpfBox, SurLpfSlider, UpmixLimits.SurLpfMinHz, UpmixLimits.SurLpfMaxHz, v => _viewModel.UpmixSurroundLpfHz = v);

    private void OnDecorrChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        SliderChanged((float)e.NewValue, DecorrBox, "F0", v => _viewModel.UpmixDecorrPct = v);
    private void OnDecorrText(object sender, TextChangedEventArgs e) =>
        TextChanged(DecorrBox, DecorrSlider, UpmixLimits.DecorrMinPct, UpmixLimits.DecorrMaxPct, v => _viewModel.UpmixDecorrPct = v);

    private void SliderChanged(float value, TextBox box, string fmt, Action<float> apply)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        apply(value);
        box.Text = value.ToString(fmt, CultureInfo.InvariantCulture);
        _isUpdating = false;
    }

    private void TextChanged(TextBox box, Slider slider, float min, float max, Action<float> apply)
    {
        if (_isUpdating) return;
        if (float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            _isUpdating = true;
            value = Math.Clamp(value, min, max);
            apply(value);
            slider.Value = value;
            _isUpdating = false;
        }
    }
}
