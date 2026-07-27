using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DSPiConsole.Core.Models;
using DSPiConsole.Settings;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace DSPiConsole;

/// <summary>
/// Control Surfaces + IR remote editor. A caps-driven window that binds physical
/// GPIO controls (buttons, switches, pots, encoders, LEDs, PWM LEDs) and an IR
/// receiver to DSP parameters. Every edit previews live on the device; Save
/// persists to flash, Revert discards. Mirrors the macOS reference app.
/// </summary>
public sealed partial class ControlSurfacesWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private readonly MainViewModel _vm;
    private AppWindow? _appWindow;

    // Fixed logical width: 20px page padding each side + 120px row labels +
    // editor controls + card chrome all fit comfortably; height stays resizable
    // for the scrolling card list.
    private const int FixedWidth = 560;

    // Editable drafts (seeded from the VM's live values). A slot is "dirty" when
    // its draft differs from the applied device state.
    private readonly CsBinding[] _drafts = new CsBinding[CsLimits.MaxBindings];
    private readonly IrCommand[] _irDrafts = new IrCommand[CsLimits.MaxIrCommands];
    private readonly string[] _nameEdits = new string[CsLimits.MaxBindings];

    private readonly HashSet<int> _expanded = new();
    private readonly HashSet<int> _irExpanded = new();

    private bool _building;
    private int? _applyingSlot;
    private bool _savingConfig;
    private int? _learningSub;

    // Per-slot / per-sub UI handles refreshed without a full rebuild.
    private readonly Dictionary<int, (Border Pill, Ellipse Dot, TextBlock Label)> _slotPills = new();
    private readonly Dictionary<int, TextBlock> _slotTitles = new();
    private readonly Dictionary<int, TextBlock> _slotSummaries = new();
    private readonly Dictionary<int, Button> _slotApply = new();
    private readonly Dictionary<int, StackPanel> _slotBodies = new();
    private readonly Dictionary<int, FrameworkElement> _slotCards = new();
    // Per-slot pin-combo refreshers: re-run a slot's GPIO pickers in place (e.g.
    // after another slot claims a pin) without recreating the card body.
    private readonly Dictionary<int, List<Action>> _pinRefreshers = new();
    // Per-slot / per-sub channel-picker relabellers, re-run when the user renames a
    // channel in the sidebar (the combo keeps its items and selection; only the
    // displayed names change).
    private readonly Dictionary<int, Action> _channelRelabel = new();
    private readonly Dictionary<int, Action> _irChannelRelabel = new();

    // Live handles to the IR receiver's command section, so IR edits can rebuild
    // just that section in place instead of tearing down the whole cards panel
    // (which resets scroll / makes the section visibly jump).
    private StackPanel? _irSectionPanel;
    private int _irSectionSlot = -1;
    private Button? _addRemoteButton;
    private TextBlock? _irCountLabel;
    private int _irLeadingCount; // non-card children (count label + optional hint)
    private readonly Dictionary<int, FrameworkElement> _irCommandCards = new();
    private readonly Dictionary<int, Button> _irLearnButtons = new();
    private readonly Dictionary<int, TextBlock> _irTitles = new();

    public ControlSurfacesWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();

        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow = appWindow;
        double dpiScale = GetDpiForWindow(hWnd) / 96.0;
        appWindow?.Resize(new Windows.Graphics.SizeInt32((int)(FixedWidth * dpiScale), (int)(780 * dpiScale)));
        if (appWindow != null)
        {
            appWindow.Title = "Control Surfaces";
            // Width is fixed; only the height may be resized.
            if (appWindow.Presenter is OverlappedPresenter presenter)
                presenter.IsMaximizable = false;
            appWindow.Changed += OnAppWindowChanged;
        }

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

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.ControlSurfacesReloaded += OnReloaded;
        _vm.ChannelNameChanged += OnChannelNameChanged;
        Closed += OnClosed;

        SeedDrafts();
        BuildAll();
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.ControlSurfacesReloaded -= OnReloaded;
        _vm.ChannelNameChanged -= OnChannelNameChanged;
        if (_appWindow != null) _appWindow.Changed -= OnAppWindowChanged;
    }

    /// <summary>A channel was renamed (sidebar edit, preset load, or a device
    /// push) — relabel everything here that shows a channel name.</summary>
    private void OnChannelNameChanged(int channelId) =>
        DispatcherQueue.TryEnqueue(RefreshChannelLabels);

    /// <summary>Snap the width back to <see cref="FixedWidth"/> if a resize (edge
    /// drag, snap layout, …) changed it; height is left alone.</summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange) return;
        double scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        int w = (int)(FixedWidth * scale);
        if (sender.Size.Width != w)
            sender.Resize(new Windows.Graphics.SizeInt32(w, sender.Size.Height));
    }

    private void OnReloaded() => DispatcherQueue.TryEnqueue(() => { SeedDrafts(); BuildAll(); });

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CsStatus)
            || e.PropertyName == nameof(MainViewModel.CsDirty))
        {
            DispatcherQueue.TryEnqueue(RefreshStatusIndicators);
        }
    }

    private void SeedDrafts()
    {
        for (int i = 0; i < CsLimits.MaxBindings; i++)
        {
            _drafts[i] = i < _vm.CsBindings.Count ? _vm.CsBindings[i].Clone() : CsBinding.Cleared();
            _nameEdits[i] = i < _vm.CsNames.Count ? _vm.CsNames[i] : "";
        }
        for (int i = 0; i < CsLimits.MaxIrCommands; i++)
            _irDrafts[i] = i < _vm.CsIrCommands.Count ? _vm.CsIrCommands[i].Clone() : new IrCommand();
    }

    // ── Top-level build ──────────────────────────────────────────────────────

    private void BuildAll()
    {
        if (!_vm.ControlSurfacesSupported || _vm.CsCaps is not { IsValid: true })
        {
            UnsupportedBar.IsOpen = true;
            BodyPanel.Visibility = Visibility.Collapsed;
            SaveBar.Visibility = Visibility.Collapsed;
            return;
        }

        UnsupportedBar.IsOpen = false;
        BodyPanel.Visibility = Visibility.Visible;
        BuildAddMenu();
        RebuildCards();
        RefreshStatusIndicators();
    }

    private void BuildAddMenu()
    {
        AddFlyout.Items.Clear();
        var caps = _vm.CsCaps!;
        foreach (CsType t in Enum.GetValues<CsType>())
        {
            if (t == CsType.None) continue;
            if ((int)t >= caps.TypeCount) continue;
            // One IR receiver max — hide once configured.
            if (t == CsType.Ir && (!_vm.CsIrSupported || AnyIrReceiver())) continue;
            var item = new MenuFlyoutItem
            {
                Text = TypeName(t),
                Tag = t,
                Icon = new FontIcon { Glyph = TypeGlyph(t) },
            };
            item.Click += (_, _) => AddControl(t);
            AddFlyout.Items.Add(item);
        }
        AddButton.IsEnabled = FirstFreeSlot() >= 0 && AddFlyout.Items.Count > 0;
    }

    private void RebuildCards()
    {
        _building = true;
        try
        {
            CardsPanel.Children.Clear();
            _slotPills.Clear();
            _slotTitles.Clear();
            _slotSummaries.Clear();
            _slotApply.Clear();
            _slotBodies.Clear();
            _slotCards.Clear();
            _pinRefreshers.Clear();
            _channelRelabel.Clear();

            int shown = 0;
            for (int slot = 0; slot < _vm.CsSlotCount; slot++)
            {
                if (!_drafts[slot].IsConfigured) continue;
                CardsPanel.Children.Add(BuildSlotCard(slot));
                shown++;
            }
            EmptyHint.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _building = false; }
        RefreshStatusIndicators();
    }

    // ── One slot card ────────────────────────────────────────────────────────

    private FrameworkElement BuildSlotCard(int slot)
    {
        var draft = _drafts[slot];
        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = _expanded.Contains(slot),
        };
        expander.Expanding += (_, _) => { _expanded.Add(slot); FrameExpandedCard(expander); };
        expander.Collapsed += (_, _) => _expanded.Remove(slot);

        expander.Header = BuildCardHeader(slot);
        expander.Content = BuildCardBody(slot);
        _slotCards[slot] = expander;
        return expander;
    }

    /// <summary>Smoothly scroll just enough to keep a card fully in view once it
    /// expands (or is newly added). The expander animates open, so its final height
    /// lands several frames after Expanding fires — track size changes for the whole
    /// animation, nudging the card into view as it grows, and stop once the size has
    /// settled. A no-op when the card is already fully visible.</summary>
    private void FrameExpandedCard(FrameworkElement card)
    {
        void Bring() => card.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });

        var settle = DispatcherQueue.CreateTimer();
        settle.Interval = TimeSpan.FromMilliseconds(150);
        SizeChangedEventHandler onSize = (_, _) =>
        {
            Bring();
            settle.Stop();
            settle.Start(); // restart the settle window on every size tick
        };
        settle.Tick += (_, _) =>
        {
            // No size change for one settle window: final layout — frame it and stop.
            settle.Stop();
            card.SizeChanged -= onSize;
            Bring();
        };
        card.SizeChanged += onSize;
        settle.Start();
    }

    private FrameworkElement BuildCardHeader(int slot)
    {
        var draft = _drafts[slot];
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Tinted type-icon badge.
        var accent = TypeColor(draft.Type);
        var badge = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x2A, accent.R, accent.G, accent.B)),
            Child = new FontIcon
            {
                Glyph = TypeGlyph(draft.Type),
                FontSize = 16,
                Foreground = new SolidColorBrush(accent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);

        // Title (custom name or type) + live binding summary.
        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        var title = new TextBlock
        {
            Text = SlotTitle(slot),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var summary = new TextBlock
        {
            Text = SlotSummary(slot),
            FontSize = 11,
            Foreground = SecondaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        titleStack.Children.Add(title);
        titleStack.Children.Add(summary);
        _slotTitles[slot] = title;
        _slotSummaries[slot] = summary;
        Grid.SetColumn(titleStack, 1);
        grid.Children.Add(titleStack);

        // Status pill (dot + label in a tinted capsule).
        var dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var pillContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        pillContent.Children.Add(dot);
        pillContent.Children.Add(label);
        var pill = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 3, 9, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = pillContent,
        };
        _slotPills[slot] = (pill, dot, label);
        Grid.SetColumn(pill, 2);
        grid.Children.Add(pill);

        // Delete.
        var del = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6),
        };
        ToolTipService.SetToolTip(del, "Remove this control");
        del.Click += (_, _) => RemoveControl(slot);
        Grid.SetColumn(del, 3);
        grid.Children.Add(del);

        return grid;
    }

    private FrameworkElement BuildCardBody(int slot)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        _slotBodies[slot] = panel;
        PopulateSlotBody(slot);
        return panel;
    }

    /// <summary>Fill (or refill) a slot card's body in place. Used instead of a full
    /// <see cref="RebuildCards"/> when only this card's options change (noun / action /
    /// operand range) so the card and scroll position don't jump.</summary>
    private void PopulateSlotBody(int slot)
    {
        if (!_slotBodies.TryGetValue(slot, out var panel)) return;
        _building = true;
        try
        {
            panel.Children.Clear();
            // Pickers below re-register fresh closures; a noun without a channel
            // target simply won't put one back.
            _pinRefreshers.Remove(slot);
            _channelRelabel.Remove(slot);
            var draft = _drafts[slot];

            panel.Children.Add(BuildIdentityRows(slot));
            panel.Children.Add(Divider());

            if (draft.Type == CsType.Ir)
            {
                // IR receiver: pin + invert, then the remote-button table.
                panel.Children.Add(BuildPinRows(slot));
                panel.Children.Add(FlagToggle(slot, CsFlags.Invert, "Active-low input (pull-up)"));
                panel.Children.Add(BuildApplyRow(slot));
                panel.Children.Add(BuildIrCommandsSection(slot));
            }
            else
            {
                var nd = _vm.CsNounDescFor(draft.Noun);

                panel.Children.Add(BuildNounRow(slot));
                if (nd != null)
                {
                    var actions = ValidActions(draft.Type, nd);
                    if (actions.Count > 1) panel.Children.Add(BuildActionRow(slot, actions));
                    if (nd.IsTargeted) panel.Children.Add(BuildTargetRows(slot, nd));
                    if (draft.Type == CsType.Button) panel.Children.Add(BuildEventRow(slot));
                    panel.Children.Add(BuildPinRows(slot));
                    var operand = BuildOperandRows(slot, nd);
                    if (operand != null) panel.Children.Add(operand);
                    panel.Children.Add(BuildFlagRows(slot, nd));
                }
                panel.Children.Add(BuildApplyRow(slot));
            }
        }
        finally { _building = false; }
        RefreshStatusIndicators();
    }

    // ── Editor rows ──────────────────────────────────────────────────────────

    /// <summary>Name row shown at the top of every slot card's body. The component
    /// type is fixed when the control is added.</summary>
    private FrameworkElement BuildIdentityRows(int slot)
    {
        var nameBox = new TextBox
        {
            PlaceholderText = "Name (optional)",
            Text = _nameEdits[slot],
            MinWidth = 220,
        };
        nameBox.LostFocus += (_, _) => CommitName(slot, nameBox.Text);
        nameBox.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) CommitName(slot, nameBox.Text); };
        return Row("Name", nameBox);
    }

    private static Border Divider() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 2, 0, 2),
        Background = Application.Current.Resources.TryGetValue("DividerStrokeColorDefaultBrush", out var b) && b is Brush br
            ? br : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
    };

    private FrameworkElement BuildNounRow(int slot)
    {
        var draft = _drafts[slot];
        var combo = new ComboBox { MinWidth = 220 };
        var caps = _vm.CsCaps!;
        int selectedIndex = -1, idx = 0;
        foreach (var (noun, nd) in AvailableNouns(draft.Type))
        {
            var item = new ComboBoxItem { Content = CsNounInfo.Name(noun), Tag = noun };
            combo.Items.Add(item);
            if (noun == draft.Noun) selectedIndex = idx;
            idx++;
        }
        combo.SelectedIndex = selectedIndex;
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is ComboBoxItem it && it.Tag is int noun)
                ChangeNoun(slot, noun);
        };
        return Row("Controls", combo);
    }

    private FrameworkElement BuildActionRow(int slot, List<CsAction> actions)
    {
        var draft = _drafts[slot];
        var combo = new ComboBox { MinWidth = 180 };
        int sel = -1;
        for (int i = 0; i < actions.Count; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = ActionName(actions[i]), Tag = actions[i] });
            if ((byte)actions[i] == draft.Action) sel = i;
        }
        if (sel < 0) sel = 0;
        combo.SelectedIndex = sel;
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is ComboBoxItem it && it.Tag is CsAction a)
                ChangeAction(slot, a);
        };
        return Row("Action", combo);
    }

    private FrameworkElement BuildTargetRows(int slot, CsNounDesc nd)
    {
        var draft = _drafts[slot];
        var panel = new StackPanel { Spacing = 8 };

        var chCombo = new ComboBox { MinWidth = 180 };
        for (int i = 0; i < nd.TargetCount; i++)
            chCombo.Items.Add(new ComboBoxItem { Content = ChannelLabel(nd.TargetKind, i), Tag = i });
        _channelRelabel[slot] = () => Relabel(chCombo, nd.TargetKind);
        chCombo.SelectedIndex = draft.Target < nd.TargetCount ? draft.Target : 0;
        chCombo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (chCombo.SelectedItem is ComboBoxItem it && it.Tag is int ch)
            { _drafts[slot].Target = (byte)ch; RefreshStatusIndicators(); }
        };
        panel.Children.Add(Row("Channel", chCombo));

        if (nd.HasBand)
        {
            var bandCombo = new ComboBox { MinWidth = 180 };
            var opts = BandOptions(draft.Noun).ToList();
            int sel = 0;
            for (int i = 0; i < opts.Count; i++)
            {
                bandCombo.Items.Add(new ComboBoxItem { Content = opts[i].label, Tag = opts[i].band });
                if (opts[i].band == draft.Index) sel = i;
            }
            bandCombo.SelectedIndex = sel;
            bandCombo.SelectionChanged += (_, _) =>
            {
                if (_building) return;
                if (bandCombo.SelectedItem is ComboBoxItem it && it.Tag is int band)
                { _drafts[slot].Index = (byte)band; RefreshStatusIndicators(); }
            };
            panel.Children.Add(Row("Band", bandCombo));
        }
        return panel;
    }

    private FrameworkElement BuildEventRow(int slot)
    {
        var draft = _drafts[slot];
        var combo = new ComboBox { MinWidth = 180 };
        combo.Items.Add(new ComboBoxItem { Content = "Press", Tag = CsEvent.Press });
        combo.Items.Add(new ComboBoxItem { Content = "Long press", Tag = CsEvent.Long });
        combo.Items.Add(new ComboBoxItem { Content = "Double press", Tag = CsEvent.Double });
        combo.SelectedIndex = Math.Clamp((int)draft.Event, 0, 2);
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is ComboBoxItem it && it.Tag is CsEvent ev)
            { _drafts[slot].Event = (byte)ev; RefreshStatusIndicators(); }
        };
        return Row("Button event", combo);
    }

    private FrameworkElement BuildPinRows(int slot)
    {
        var draft = _drafts[slot];
        var caps = _vm.CsCaps!;
        var typeDesc = caps.DescFor(draft.Type);
        bool twoPins = typeDesc?.PinCount == 2;
        bool adcOnly = typeDesc?.PinClass == CsPinClass.Adc;

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Row(twoPins ? "GPIO A" : "GPIO",
            PinCombo(slot, adcOnly, isSecond: false)));
        if (twoPins)
            panel.Children.Add(Row("GPIO B", PinCombo(slot, adcOnly, isSecond: true)));
        return panel;
    }

    private ComboBox PinCombo(int slot, bool adcOnly, bool isSecond)
    {
        var combo = new ComboBox { MinWidth = 160 };

        // Fill (or refill) the candidate list. Called on build and again in place
        // whenever another slot claims/frees a pin, so the options stay current
        // without recreating the card body (which would flash its Apply buttons).
        void Populate()
        {
            var draft = _drafts[slot];
            byte current = isSecond ? draft.Gpio1 : draft.Gpio0;
            var candidates = FreePins(slot, adcOnly).ToList();
            if (current != CsLimits.GpioUnused && !candidates.Contains(current)) candidates.Add(current);
            candidates.Sort();

            combo.Items.Clear();
            int sel = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                combo.Items.Add(new ComboBoxItem { Content = $"GPIO {candidates[i]}", Tag = candidates[i] });
                if (candidates[i] == current) sel = i;
            }
            combo.SelectedIndex = sel;
        }
        Populate();

        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is ComboBoxItem it && it.Tag is byte pin)
            {
                if (isSecond) _drafts[slot].Gpio1 = pin; else _drafts[slot].Gpio0 = pin;
                RefreshStatusIndicators();
            }
        };

        if (!_pinRefreshers.TryGetValue(slot, out var list))
            _pinRefreshers[slot] = list = new List<Action>();
        list.Add(Populate);
        return combo;
    }

    private FrameworkElement? BuildOperandRows(int slot, CsNounDesc nd)
    {
        var draft = _drafts[slot];
        var action = (CsAction)draft.Action;

        // Span (pot ADJUST / PWM IND_LEVEL): rangeMin/rangeMax over the noun range.
        if (action is CsAction.Adjust or CsAction.IndLevel && nd.Kind == CsKind.Continuous)
            return BuildSpanRows(slot, nd);

        // Step (encoder STEP, button INC/DEC).
        if (action is CsAction.Step or CsAction.Inc or CsAction.Dec)
            return BuildStepRow(slot, nd);

        // Value (SET / MOMENTARY / IND_EQUALS / IND_ABOVE).
        if (action is CsAction.Set or CsAction.Momentary or CsAction.IndEquals or CsAction.IndAbove)
            return BuildValueRow(slot, nd);

        // Toggle / Follow / Trigger have no operand.
        return null;
    }

    private FrameworkElement BuildSpanRows(int slot, CsNounDesc nd)
    {
        var draft = _drafts[slot];
        var panel = new StackPanel { Spacing = 8 };
        bool custom = !(draft.RangeMin == 0 && draft.RangeMax == 0);

        var toggle = new CheckBox { Content = "Limit to a custom range", IsChecked = custom };
        panel.Children.Add(toggle);

        var minBox = NumberField(CsWire.DecodeValue(draft.RangeMin, nd.Unit), nd.Unit, v =>
        { _drafts[slot].RangeMin = CsWire.EncodeValue(v, nd.Unit); RefreshStatusIndicators(); });
        var maxBox = NumberField(CsWire.DecodeValue(draft.RangeMax, nd.Unit), nd.Unit, v =>
        { _drafts[slot].RangeMax = CsWire.EncodeValue(v, nd.Unit); RefreshStatusIndicators(); });

        var minRow = Row("Minimum", minBox);
        var maxRow = Row("Maximum", maxBox);
        minRow.Visibility = maxRow.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(minRow);
        panel.Children.Add(maxRow);

        toggle.Checked += (_, _) =>
        {
            if (_building) return;
            // Seed a sensible span from the noun's full range.
            _drafts[slot].RangeMin = nd.MinQ; _drafts[slot].RangeMax = nd.MaxQ;
            PopulateSlotBody(slot);
        };
        toggle.Unchecked += (_, _) =>
        {
            if (_building) return;
            _drafts[slot].RangeMin = 0; _drafts[slot].RangeMax = 0;
            PopulateSlotBody(slot);
        };
        return panel;
    }

    private FrameworkElement BuildStepRow(int slot, CsNounDesc nd)
    {
        var draft = _drafts[slot];
        if (nd.Kind == CsKind.Enum || nd.Unit == CsUnit.None)
        {
            var box = NumberField(draft.Step == 0 ? CsWire.DefaultStep(nd.Unit) : CsWire.DecodeStep(draft.Step, nd.Unit), CsUnit.None, v =>
            { _drafts[slot].Step = CsWire.EncodeStep(v, nd.Unit); RefreshStatusIndicators(); });
            return Row("Step (positions)", box);
        }
        // Hz/Q step is in octaves; dB/%/ms linear.
        string label = nd.Unit is CsUnit.Hz or CsUnit.Q ? "Step (octaves)" : StepLabel(nd.Unit);
        var stepBox = NumberField(CsWire.DecodeStep(draft.Step, nd.Unit), CsUnit.None, v =>
        { _drafts[slot].Step = CsWire.EncodeStep(v, nd.Unit); RefreshStatusIndicators(); });
        return Row(label, stepBox);
    }

    private FrameworkElement BuildValueRow(int slot, CsNounDesc nd)
    {
        var draft = _drafts[slot];
        if (nd.Kind == CsKind.Bool)
        {
            var combo = new ComboBox { MinWidth = 120 };
            combo.Items.Add(new ComboBoxItem { Content = "Off", Tag = (short)0 });
            combo.Items.Add(new ComboBoxItem { Content = "On", Tag = (short)1 });
            combo.SelectedIndex = draft.Value != 0 ? 1 : 0;
            combo.SelectionChanged += (_, _) =>
            {
                if (_building) return;
                if (combo.SelectedItem is ComboBoxItem it && it.Tag is short v)
                { _drafts[slot].Value = v; RefreshStatusIndicators(); }
            };
            return Row("Value", combo);
        }
        if (nd.Kind == CsKind.Enum)
        {
            var combo = new ComboBox { MinWidth = 120 };
            for (int i = 0; i < Math.Max(1, (int)nd.EnumCount); i++)
                combo.Items.Add(new ComboBoxItem { Content = CsNounInfo.EnumLabel(draft.Noun, i), Tag = (short)i });
            combo.SelectedIndex = Math.Clamp((int)draft.Value, 0, Math.Max(0, nd.EnumCount - 1));
            combo.SelectionChanged += (_, _) =>
            {
                if (_building) return;
                if (combo.SelectedItem is ComboBoxItem it && it.Tag is short v)
                { _drafts[slot].Value = v; RefreshStatusIndicators(); }
            };
            return Row("Value", combo);
        }
        var box = NumberField(CsWire.DecodeValue(draft.Value, nd.Unit), nd.Unit, v =>
        { _drafts[slot].Value = CsWire.EncodeValue(v, nd.Unit); RefreshStatusIndicators(); });
        return Row(ValueLabel(nd.Unit), box);
    }

    private FrameworkElement BuildFlagRows(int slot, CsNounDesc nd)
    {
        var draft = _drafts[slot];
        var action = (CsAction)draft.Action;
        var panel = new StackPanel { Spacing = 2 };

        panel.Children.Add(FlagToggle(slot, CsFlags.Invert,
            draft.Type is CsType.Led or CsType.LedPwm ? "Active-low output" : "Active-low input (pull-up)"));

        if (draft.Type is CsType.Pot or CsType.Encoder)
            panel.Children.Add(FlagToggle(slot, CsFlags.Reverse, "Reverse direction"));
        if (draft.Type == CsType.Encoder)
            panel.Children.Add(FlagToggle(slot, CsFlags.Accel, "Acceleration (fast rotation = bigger steps)"));
        if (nd.Kind == CsKind.Enum && action is CsAction.Step or CsAction.Inc or CsAction.Dec)
            panel.Children.Add(FlagToggle(slot, CsFlags.Wrap, "Wrap around at the ends"));
        if (draft.Type == CsType.Button && action is CsAction.Inc or CsAction.Dec && draft.Event == (byte)CsEvent.Press)
            panel.Children.Add(FlagToggle(slot, CsFlags.Repeat, "Auto-repeat while held"));

        return panel;
    }

    private CheckBox FlagToggle(int slot, CsFlags flag, string label)
    {
        var cb = new CheckBox { Content = label, IsChecked = _drafts[slot].Flags.HasFlag(flag) };
        cb.Checked += (_, _) => { _drafts[slot].Flags |= flag; RefreshStatusIndicators(); };
        cb.Unchecked += (_, _) => { _drafts[slot].Flags &= ~flag; RefreshStatusIndicators(); };
        return cb;
    }

    private FrameworkElement BuildApplyRow(int slot)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (_drafts[slot].Type == CsType.Ir)
        {
            var addBtn = new Button { Content = "Add Remote Button" };
            addBtn.Click += (_, _) => AddIrCommand(slot);
            addBtn.IsEnabled = _vm.CsStatus?.IsSlotActive(slot) == true && FirstFreeIrSub() >= 0;
            _addRemoteButton = addBtn;
            panel.Children.Add(addBtn);
        }
        var revert = new Button { Content = "Revert" };
        revert.Click += (_, _) => RevertSlot(slot);
        var apply = new Button { Content = "Apply", Style = AccentStyle };
        apply.Click += (_, _) => _ = ApplySlotAsync(slot);
        _slotApply[slot] = apply;
        panel.Children.Add(revert);
        panel.Children.Add(apply);
        return panel;
    }

    // ── IR command table ─────────────────────────────────────────────────────

    private FrameworkElement BuildIrCommandsSection(int slot)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        _irSectionPanel = panel;
        _irSectionSlot = slot;
        PopulateIrCommandsSection();
        return panel;
    }

    /// <summary>Fill (or refill) the IR receiver's remote-button list in place.
    /// Used instead of a full <see cref="RebuildCards"/> for IR-only edits so the
    /// receiver card and scroll position don't jump.</summary>
    private void PopulateIrCommandsSection()
    {
        if (_irSectionPanel is not { } panel) return;
        int slot = _irSectionSlot;
        _building = true;
        try
        {
            panel.Children.Clear();
            _irCommandCards.Clear();
            _irLearnButtons.Clear();
            _irTitles.Clear();
            _irChannelRelabel.Clear();
            bool receiverLive = _vm.CsStatus?.IsSlotActive(slot) == true;

            _irCountLabel = new TextBlock { Text = IrCountText(), FontWeight = FontWeights.SemiBold };
            panel.Children.Add(_irCountLabel);
            _irLeadingCount = 1;
            if (!receiverLive)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Apply the receiver first, then learn remote buttons.",
                    FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    Foreground = SecondaryBrush,
                });
                _irLeadingCount = 2;
            }

            for (int sub = 0; sub < _vm.CsIrMax; sub++)
                if (IrSubShown(sub))
                    panel.Children.Add(BuildIrCommandCard(sub, receiverLive));

            UpdateAddRemoteButtonState();
        }
        finally { _building = false; }
    }

    private bool IrSubShown(int sub) => _irDrafts[sub].IsConfigured || _irExpanded.Contains(sub);

    private string IrCountText() => $"Remote Buttons ({ConfiguredIrCount()}/{_vm.CsIrMax})";

    private void UpdateIrCount()
    {
        if (_irCountLabel != null) _irCountLabel.Text = IrCountText();
    }

    private void UpdateAddRemoteButtonState()
    {
        if (_addRemoteButton != null)
            _addRemoteButton.IsEnabled =
                _vm.CsStatus?.IsSlotActive(_irSectionSlot) == true && FirstFreeIrSub() >= 0;
    }

    /// <summary>While a learn is in progress every other card's Learn button is
    /// disabled; refresh their enabled state in place without rebuilding them.</summary>
    private void RefreshIrLearnButtons()
    {
        bool receiverLive = _vm.CsStatus?.IsSlotActive(_irSectionSlot) == true;
        foreach (var (_, btn) in _irLearnButtons)
            btn.IsEnabled = receiverLive && _learningSub == null && !_savingConfig;
    }

    /// <summary>Build and insert one remote-button card at its sub-ordered position,
    /// leaving the sibling cards untouched.</summary>
    private void InsertIrCommandCard(int sub)
    {
        if (_irSectionPanel is not { } panel) return;
        bool receiverLive = _vm.CsStatus?.IsSlotActive(_irSectionSlot) == true;
        int index = _irLeadingCount;
        for (int s = 0; s < sub; s++)
            if (_irCommandCards.ContainsKey(s)) index++;
        _building = true;
        try { panel.Children.Insert(index, BuildIrCommandCard(sub, receiverLive)); }
        finally { _building = false; }
    }

    /// <summary>Remove one remote-button card and drop its handles.</summary>
    private void RemoveIrCommandCard(int sub)
    {
        if (_irCommandCards.TryGetValue(sub, out var card))
            _irSectionPanel?.Children.Remove(card);
        _irCommandCards.Remove(sub);
        _irLearnButtons.Remove(sub);
        _irTitles.Remove(sub);
        _irChannelRelabel.Remove(sub);
    }

    /// <summary>Rebuild a single remote-button card in place (chip, learn state,
    /// operand rows) without touching its siblings.</summary>
    private void RebuildIrCommandCard(int sub)
    {
        if (_irSectionPanel is not { } panel) return;
        if (!_irCommandCards.TryGetValue(sub, out var old)) return;
        int index = panel.Children.IndexOf(old);
        if (index < 0) return;
        bool receiverLive = _vm.CsStatus?.IsSlotActive(_irSectionSlot) == true;
        _building = true;
        try
        {
            var fresh = BuildIrCommandCard(sub, receiverLive);
            panel.Children.RemoveAt(index);
            panel.Children.Insert(index, fresh);
        }
        finally { _building = false; }
    }

    private FrameworkElement BuildIrCommandCard(int sub, bool receiverLive)
    {
        var draft = _irDrafts[sub];
        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = _irExpanded.Contains(sub),
        };
        expander.Expanding += (_, _) => { _irExpanded.Add(sub); FrameExpandedCard(expander); };
        expander.Collapsed += (_, _) => _irExpanded.Remove(sub);

        // Header: binding summary + learned-code chip + delete.
        var hgrid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 4, 0, 4) };
        hgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = IrCommandTitle(sub),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _irTitles[sub] = title;
        Grid.SetColumn(title, 0);
        hgrid.Children.Add(title);

        var chipColor = draft.IsConfigured
            ? Color.FromArgb(255, 100, 200, 140)
            : Color.FromArgb(255, 0x90, 0x90, 0x90);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x20, chipColor.R, chipColor.G, chipColor.B)),
            Child = new TextBlock
            {
                Text = draft.CodeLabel,
                FontSize = 11,
                FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
                Foreground = new SolidColorBrush(chipColor),
            },
        };
        Grid.SetColumn(chip, 1);
        hgrid.Children.Add(chip);
        var delc = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 13 },
            Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
        };
        delc.Click += (_, _) => RemoveIrCommand(sub);
        Grid.SetColumn(delc, 2);
        hgrid.Children.Add(delc);
        expander.Header = hgrid;

        // Body: learn + operand editor.
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };

        var learnPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (_learningSub == sub)
        {
            _irLearnButtons.Remove(sub); // this card shows a Cancel, not a Learn button
            learnPanel.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
            learnPanel.Children.Add(new TextBlock
            {
                Text = "Point the remote at the receiver…", VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
            });
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, _) => CancelLearn();
            learnPanel.Children.Add(cancel);
        }
        else
        {
            var learn = new Button { Content = draft.IsConfigured ? "Re-learn" : "Learn Button" };
            learn.IsEnabled = receiverLive && _learningSub == null && !_savingConfig;
            learn.Click += (_, _) => StartLearn(sub);
            learnPanel.Children.Add(learn);
            _irLearnButtons[sub] = learn;
        }
        body.Children.Add(learnPanel);

        // Noun / action / target / operand for the IR command (button-shaped).
        body.Children.Add(BuildIrNounRow(sub));
        var nd = _vm.CsNounDescFor(draft.Noun);
        if (nd != null)
        {
            var actions = ValidIrActions(nd);
            if (actions.Count > 1) body.Children.Add(BuildIrActionRow(sub, actions));
            if (nd.IsTargeted) body.Children.Add(BuildIrTargetRows(sub, nd));
            var op = BuildIrOperand(sub, nd);
            if (op != null) body.Children.Add(op);
        }

        var applyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0),
        };
        var apply = new Button { Content = "Apply", Style = AccentStyle };
        apply.IsEnabled = draft.IsConfigured && receiverLive;
        apply.Click += (_, _) => _ = ApplyIrCommandAsync(sub);
        applyRow.Children.Add(apply);
        body.Children.Add(applyRow);

        expander.Content = body;
        _irCommandCards[sub] = expander;
        return expander;
    }

    private FrameworkElement BuildIrNounRow(int sub)
    {
        var draft = _irDrafts[sub];
        var combo = new ComboBox { MinWidth = 200 };
        int sel = -1, idx = 0;
        foreach (var (noun, nd) in AvailableIrNouns())
        {
            combo.Items.Add(new ComboBoxItem { Content = CsNounInfo.Name(noun), Tag = noun });
            if (noun == draft.Noun) sel = idx;
            idx++;
        }
        combo.SelectedIndex = sel;
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is ComboBoxItem it && it.Tag is int noun)
            {
                _irDrafts[sub].Noun = (byte)noun;
                var nd = _vm.CsNounDescFor(noun);
                var acts = nd != null ? ValidIrActions(nd) : new List<CsAction>();
                _irDrafts[sub].Action = acts.Count > 0 ? (byte)acts[0] : (byte)0;
                RebuildIrCommandCard(sub);
            }
        };
        return Row("Controls", combo);
    }

    private FrameworkElement BuildIrActionRow(int sub, List<CsAction> actions)
    {
        var draft = _irDrafts[sub];
        var combo = new ComboBox { MinWidth = 160 };
        int sel = 0;
        for (int i = 0; i < actions.Count; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = ActionName(actions[i]), Tag = actions[i] });
            if ((byte)actions[i] == draft.Action) sel = i;
        }
        combo.SelectedIndex = sel;
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is ComboBoxItem it && it.Tag is CsAction a)
            { _irDrafts[sub].Action = (byte)a; RebuildIrCommandCard(sub); }
        };
        return Row("Action", combo);
    }

    private FrameworkElement BuildIrTargetRows(int sub, CsNounDesc nd)
    {
        var draft = _irDrafts[sub];
        var panel = new StackPanel { Spacing = 8 };
        var chCombo = new ComboBox { MinWidth = 160 };
        for (int i = 0; i < nd.TargetCount; i++)
            chCombo.Items.Add(new ComboBoxItem { Content = ChannelLabel(nd.TargetKind, i), Tag = i });
        _irChannelRelabel[sub] = () => Relabel(chCombo, nd.TargetKind);
        chCombo.SelectedIndex = draft.Target < nd.TargetCount ? draft.Target : 0;
        chCombo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (chCombo.SelectedItem is ComboBoxItem it && it.Tag is int ch)
            { _irDrafts[sub].Target = (byte)ch; UpdateIrTitle(sub); }
        };
        panel.Children.Add(Row("Channel", chCombo));

        if (nd.HasBand)
        {
            var bandCombo = new ComboBox { MinWidth = 160 };
            var opts = BandOptions(draft.Noun).ToList();
            int sel = 0;
            for (int i = 0; i < opts.Count; i++)
            {
                bandCombo.Items.Add(new ComboBoxItem { Content = opts[i].label, Tag = opts[i].band });
                if (opts[i].band == draft.Index) sel = i;
            }
            bandCombo.SelectedIndex = sel;
            bandCombo.SelectionChanged += (_, _) =>
            {
                if (_building) return;
                if (bandCombo.SelectedItem is ComboBoxItem it && it.Tag is int band)
                    _irDrafts[sub].Index = (byte)band;
            };
            panel.Children.Add(Row("Band", bandCombo));
        }
        return panel;
    }

    private FrameworkElement? BuildIrOperand(int sub, CsNounDesc nd)
    {
        var draft = _irDrafts[sub];
        var action = (CsAction)draft.Action;
        if (action is CsAction.Inc or CsAction.Dec)
        {
            string label = nd.Unit is CsUnit.Hz or CsUnit.Q ? "Step (octaves)"
                : nd.Unit == CsUnit.None ? "Step (positions)" : StepLabel(nd.Unit);
            var box = NumberField(CsWire.DecodeStep(draft.Step, nd.Unit), CsUnit.None, v =>
                _irDrafts[sub].Step = CsWire.EncodeStep(v, nd.Unit));
            return Row(label, box);
        }
        if (action is CsAction.Set or CsAction.Momentary)
        {
            if (nd.Kind == CsKind.Bool)
            {
                var combo = new ComboBox { MinWidth = 120 };
                combo.Items.Add(new ComboBoxItem { Content = "Off", Tag = (short)0 });
                combo.Items.Add(new ComboBoxItem { Content = "On", Tag = (short)1 });
                combo.SelectedIndex = draft.Value != 0 ? 1 : 0;
                combo.SelectionChanged += (_, _) =>
                {
                    if (_building) return;
                    if (combo.SelectedItem is ComboBoxItem it && it.Tag is short v) _irDrafts[sub].Value = v;
                };
                return Row("Value", combo);
            }
            if (nd.Kind == CsKind.Enum)
            {
                var combo = new ComboBox { MinWidth = 120 };
                for (int i = 0; i < Math.Max(1, (int)nd.EnumCount); i++)
                    combo.Items.Add(new ComboBoxItem { Content = CsNounInfo.EnumLabel(draft.Noun, i), Tag = (short)i });
                combo.SelectedIndex = Math.Clamp((int)draft.Value, 0, Math.Max(0, nd.EnumCount - 1));
                combo.SelectionChanged += (_, _) =>
                {
                    if (_building) return;
                    if (combo.SelectedItem is ComboBoxItem it && it.Tag is short v) _irDrafts[sub].Value = v;
                };
                return Row("Value", combo);
            }
            var box = NumberField(CsWire.DecodeValue(draft.Value, nd.Unit), nd.Unit, v =>
                _irDrafts[sub].Value = CsWire.EncodeValue(v, nd.Unit));
            return Row(ValueLabel(nd.Unit), box);
        }
        return null; // Toggle / Trigger
    }

    // ── Actions: add / remove / change ───────────────────────────────────────

    private async void AddControl(CsType type)
    {
        int slot = FirstFreeSlot();
        if (slot < 0) return;
        _drafts[slot] = MakeDefaultBinding(type, slot);
        _nameEdits[slot] = "";
        _expanded.Add(slot);
        // Insert just the new card instead of rebuilding the panel, so existing
        // cards don't reload.
        InsertSlotCard(slot);
        if (_slotCards.TryGetValue(slot, out var card)) FrameExpandedCard(card);
        BuildAddMenu();
        // A freshly-added control is seeded valid — apply immediately.
        await ApplySlotAsync(slot);
    }

    /// <summary>Build one slot's card and insert it into <c>CardsPanel</c> at the
    /// position matching slot order, leaving the other cards untouched.</summary>
    private void InsertSlotCard(int slot)
    {
        int index = 0;
        for (int s = 0; s < slot; s++)
            if (_drafts[s].IsConfigured) index++;
        CardsPanel.Children.Insert(index, BuildSlotCard(slot));
        EmptyHint.Visibility = Visibility.Collapsed;
    }

    private void ChangeNoun(int slot, int noun)
    {
        var b = _drafts[slot];
        b.Noun = (byte)noun;
        var nd = _vm.CsNounDescFor(noun);
        if (nd != null)
        {
            var acts = ValidActions(b.Type, nd);
            if (!acts.Contains((CsAction)b.Action))
                b.Action = acts.Count > 0 ? (byte)acts[0] : (byte)0;
            if (b.Target >= nd.TargetCount) b.Target = 0;
        }
        // Reset operands to defaults for the new noun.
        b.Value = 0; b.Step = 0; b.RangeMin = 0; b.RangeMax = 0;
        PopulateSlotBody(slot);
    }

    private void ChangeAction(int slot, CsAction action)
    {
        _drafts[slot].Action = (byte)action;
        _drafts[slot].Value = 0; _drafts[slot].Step = 0;
        _drafts[slot].RangeMin = 0; _drafts[slot].RangeMax = 0;
        PopulateSlotBody(slot);
    }

    private async void RemoveControl(int slot)
    {
        // Unapplied slot → just drop the local draft and its card. It never claimed a
        // pin (only live slots do), so no other card needs refreshing.
        if (!_vm.CsBindings[slot].IsConfigured)
        {
            _drafts[slot] = CsBinding.Cleared();
            _nameEdits[slot] = "";
            RemoveSlotCard(slot);
            BuildAddMenu();
            return;
        }
        _drafts[slot] = CsBinding.Cleared();
        _nameEdits[slot] = "";
        RemoveSlotCard(slot);
        await Task.Run(() =>
        {
            _vm.SetCsBinding(slot, CsBinding.Cleared());
            if (!string.IsNullOrEmpty(_vm.CsNames[slot])) _vm.SetCsName(slot, "");
        });
        HardwarePins.RaisePinAssignmentsChanged();
        SeedDraftFrom(slot);
        // The removed slot freed its GPIO(s) — refresh the other cards' pin lists in
        // place so those pins reappear, without reloading their bodies.
        RefreshOtherSlotPins(slot);
        RefreshStatusIndicators();
        BuildAddMenu();
    }

    /// <summary>Remove one slot's card element and drop its per-slot UI handles,
    /// leaving the other cards untouched.</summary>
    private void RemoveSlotCard(int slot)
    {
        if (_slotCards.TryGetValue(slot, out var card))
            CardsPanel.Children.Remove(card);
        _slotCards.Remove(slot);
        _slotBodies.Remove(slot);
        _slotPills.Remove(slot);
        _slotTitles.Remove(slot);
        _slotSummaries.Remove(slot);
        _slotApply.Remove(slot);
        _pinRefreshers.Remove(slot);
        _channelRelabel.Remove(slot);
        _expanded.Remove(slot);
        if (slot == _irSectionSlot)
        {
            // The IR receiver card (which hosts the remote-button section) is gone.
            _irSectionPanel = null;
            _irSectionSlot = -1;
            _addRemoteButton = null;
            _irCountLabel = null;
            _irCommandCards.Clear();
            _irLearnButtons.Clear();
            _irTitles.Clear();
            _irChannelRelabel.Clear();
        }
        if (CardsPanel.Children.Count == 0) EmptyHint.Visibility = Visibility.Visible;
    }

    /// <summary>Rebuild one slot's whole card (header + body) in place, e.g. after a
    /// type change or revert alters the type badge. Other cards are left untouched.</summary>
    private void RebuildSlotCard(int slot)
    {
        if (!_slotCards.TryGetValue(slot, out var old)) return;
        int index = CardsPanel.Children.IndexOf(old);
        if (index < 0) return;
        var fresh = BuildSlotCard(slot); // re-registers this slot's UI handles
        CardsPanel.Children.RemoveAt(index);
        CardsPanel.Children.Insert(index, fresh);
    }

    private void CommitName(int slot, string text)
    {
        _nameEdits[slot] = text ?? "";
        RefreshStatusIndicators();
    }

    private void RevertSlot(int slot)
    {
        SeedDraftFrom(slot);
        // Reverting only restores this slot's draft; other cards are unaffected.
        RebuildSlotCard(slot);
    }

    private async Task ApplySlotAsync(int slot)
    {
        if (_applyingSlot != null) return;
        _applyingSlot = slot;
        RefreshStatusIndicators();

        var binding = _drafts[slot].Clone();
        bool bindingChanged = !binding.WireEquals(_vm.CsBindings[slot]);
        bool nameChanged = !string.Equals(_nameEdits[slot], _vm.CsNames[slot], StringComparison.Ordinal);
        string name = _nameEdits[slot];

        byte status = CsStatus.Success;
        await Task.Run(() =>
        {
            if (bindingChanged) status = _vm.SetCsBinding(slot, binding);
            if (status == CsStatus.Success && nameChanged) status = _vm.SetCsName(slot, name);
        });

        _applyingSlot = null;
        HardwarePins.RaisePinAssignmentsChanged();
        SeedDraftFrom(slot);
        // Fully refresh only the applied card. Applying makes this slot live, so it now
        // claims its GPIO(s) — the other cards just need their pin lists refreshed in
        // place (not their whole bodies, which would flash their Apply buttons).
        PopulateSlotBody(slot);
        RefreshOtherSlotPins(slot);
        BuildAddMenu();

        if (status != CsStatus.Success)
            ShowToast(CsStatus.Message(status));
    }

    /// <summary>Re-run every other slot's GPIO pickers in place so a pin this slot just
    /// claimed (or freed) drops out of (or back into) their option lists, without
    /// recreating their card bodies.</summary>
    private void RefreshOtherSlotPins(int exceptSlot)
    {
        _building = true;
        try
        {
            foreach (var (slot, refreshers) in _pinRefreshers)
            {
                if (slot == exceptSlot) continue;
                foreach (var refresh in refreshers) refresh();
            }
        }
        finally { _building = false; }
    }

    // ── IR command actions ───────────────────────────────────────────────────

    private void AddIrCommand(int slot)
    {
        int sub = FirstFreeIrSub();
        if (sub < 0) return;
        var draft = new IrCommand();
        var noun = AvailableIrNouns().FirstOrDefault();
        if (noun.nd != null)
        {
            draft.Noun = (byte)noun.noun;
            var acts = ValidIrActions(noun.nd);
            draft.Action = acts.Count > 0 ? (byte)acts[0] : (byte)0;
        }
        _irDrafts[sub] = draft;
        _irExpanded.Add(sub);
        // Insert just the new remote-button card; siblings stay put.
        InsertIrCommandCard(sub);
        if (_irCommandCards.TryGetValue(sub, out var card)) FrameExpandedCard(card);
        UpdateIrCount();
        UpdateAddRemoteButtonState();
    }

    private async void RemoveIrCommand(int sub)
    {
        bool wasLive = _vm.CsIrCommands[sub].IsConfigured;
        _irDrafts[sub] = new IrCommand();
        _irExpanded.Remove(sub);
        // Remove just this card; siblings stay put.
        RemoveIrCommandCard(sub);
        UpdateIrCount();
        UpdateAddRemoteButtonState();
        if (wasLive)
        {
            await Task.Run(() => _vm.SetCsIrCommand(sub, new IrCommand()));
            _irDrafts[sub] = _vm.CsIrCommands[sub].Clone();
            UpdateIrCount();
            UpdateAddRemoteButtonState();
        }
    }

    private async Task ApplyIrCommandAsync(int sub)
    {
        var cmd = _irDrafts[sub].Clone();
        if (!cmd.IsConfigured) { ShowToast("Learn a code before applying this button."); return; }
        byte status = await Task.Run(() => _vm.SetCsIrCommand(sub, cmd));
        _irDrafts[sub] = _vm.CsIrCommands[sub].Clone();
        RebuildIrCommandCard(sub);
        UpdateIrCount();
        if (status != CsStatus.Success) ShowToast(CsStatus.Message(status));
    }

    private void StartLearn(int sub)
    {
        if (_learningSub != null) return;
        _learningSub = sub;
        RebuildIrCommandCard(sub);   // show the learning spinner on this card
        RefreshIrLearnButtons();     // disable the other cards' Learn buttons

        var proto = _irDrafts[sub];
        _ = Task.Run(async () =>
        {
            bool armed = _vm.CsIrLearnArm();
            if (!armed)
            {
                DispatcherQueue.TryEnqueue(() => FinishLearn(sub, null, "No IR receiver is active."));
                return;
            }
            CsIrLearnResult? result = null;
            for (int i = 0; i < 115 && _learningSub == sub; i++)
            {
                await Task.Delay(100);
                var r = _vm.CsIrLearnRead();
                if (r != null && r.State != CsIrLearnState.Armed) { result = r; break; }
            }
            DispatcherQueue.TryEnqueue(() => FinishLearn(sub, result, null));
        });
    }

    private void FinishLearn(int sub, CsIrLearnResult? result, string? error)
    {
        if (_learningSub != sub) return;
        _learningSub = null;

        if (error == null)
        {
            if (result != null && result.IsDone && result.Code != 0)
            {
                _irDrafts[sub].Proto = result.Proto;
                _irDrafts[sub].Code = result.Code;
                ShowToast($"Learned a {CsWire.IrProtocolName(result.Proto)} code. Apply to keep it.");
            }
            else if (result != null && result.IsTimeout)
            {
                ShowToast("No remote button was detected.");
            }
            else
            {
                ShowToast("Learn stopped.");
            }
        }
        else ShowToast(error);

        // Restore this card (learned chip / Learn button) and re-enable the others.
        RebuildIrCommandCard(sub);
        UpdateIrCount();
        RefreshIrLearnButtons();
    }

    private void CancelLearn()
    {
        int? sub = _learningSub;
        _learningSub = null;
        _ = Task.Run(() => _vm.CsIrLearnCancel());
        if (sub is int s) RebuildIrCommandCard(s);
        RefreshIrLearnButtons();
    }

    // ── Save / revert all ────────────────────────────────────────────────────

    private async void OnSaveAllClick(object sender, RoutedEventArgs e)
    {
        if (_savingConfig) return;
        _savingConfig = true;
        SaveRing.IsActive = true;
        SaveAllButton.IsEnabled = RevertAllButton.IsEnabled = false;

        byte status = await Task.Run(() => _vm.CsSave());

        _savingConfig = false;
        SaveRing.IsActive = false;
        RefreshStatusIndicators();
        if (status != CsStatus.Success) ShowToast(CsStatus.Message(status));
        else ShowToast("Saved to device.");
    }

    private async void OnRevertAllClick(object sender, RoutedEventArgs e)
    {
        if (_savingConfig) return;
        _savingConfig = true;
        SaveRing.IsActive = true;
        SaveAllButton.IsEnabled = RevertAllButton.IsEnabled = false;

        await Task.Run(() => _vm.CsRevert());

        _savingConfig = false;
        SaveRing.IsActive = false;
        HardwarePins.RaisePinAssignmentsChanged();
        // OnReloaded re-seeds drafts and rebuilds.
    }

    // ── Status refresh ───────────────────────────────────────────────────────

    private void RefreshStatusIndicators()
    {
        var status = _vm.CsStatus;
        foreach (var (slot, pill) in _slotPills)
        {
            if (SlotDirty(slot))
                SetPill(pill, "Pending", Color.FromArgb(255, 240, 180, 90));
            else if (status?.IsSlotActive(slot) == true)
                SetPill(pill, "Active", Color.FromArgb(255, 100, 200, 140));
            else
                SetPill(pill, "Inactive", Color.FromArgb(255, 240, 180, 90));
        }
        foreach (var (slot, title) in _slotTitles) title.Text = SlotTitle(slot);
        foreach (var (slot, summary) in _slotSummaries) summary.Text = SlotSummary(slot);
        foreach (var (slot, apply) in _slotApply)
            apply.IsEnabled = SlotDirty(slot) && _applyingSlot == null && !_savingConfig;

        bool dirtyAll = _vm.CsDirty;
        SaveBar.Visibility = dirtyAll ? Visibility.Visible : Visibility.Collapsed;
        SaveAllButton.IsEnabled = RevertAllButton.IsEnabled = dirtyAll && !_savingConfig;
    }

    private bool SlotDirty(int slot)
    {
        if (slot >= _vm.CsBindings.Count) return false;
        if (!_drafts[slot].WireEquals(_vm.CsBindings[slot])) return true;
        if (!string.Equals(_nameEdits[slot], _vm.CsNames[slot], StringComparison.Ordinal)) return true;
        return false;
    }

    private void ShowToast(string msg)
    {
        SaveStatusText.Text = msg;
        SaveStatusText.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
        if (SaveBar.Visibility != Visibility.Visible)
        {
            // Surface the message via the InfoBar when the save bar is hidden.
            LoadingBar.Title = "";
            LoadingBar.Message = msg;
            LoadingBar.Severity = InfoBarSeverity.Informational;
            LoadingBar.IsOpen = true;
        }
    }

    // ── Helpers: seeding, pins, caps queries ─────────────────────────────────

    private void SeedDraftFrom(int slot)
    {
        _drafts[slot] = _vm.CsBindings[slot].Clone();
        _nameEdits[slot] = _vm.CsNames[slot];
    }

    private int FirstFreeSlot()
    {
        for (int i = 0; i < _vm.CsSlotCount; i++)
            if (!_drafts[i].IsConfigured) return i;
        return -1;
    }

    private int FirstFreeIrSub()
    {
        // A sub is taken once it's learned (IsConfigured) OR while an unlearned draft
        // is still being edited (expanded) — otherwise a second Add would reuse and
        // overwrite the first not-yet-learned button's sub.
        for (int i = 0; i < _vm.CsIrMax; i++)
            if (!_irDrafts[i].IsConfigured && !_irExpanded.Contains(i)) return i;
        return -1;
    }

    private int ConfiguredIrCount() => _irDrafts.Take(_vm.CsIrMax).Count(c => c.IsConfigured);

    private bool AnyIrReceiver()
    {
        for (int i = 0; i < _vm.CsSlotCount; i++)
            if (_drafts[i].Type == CsType.Ir) return true;
        return false;
    }

    /// <summary>GPIOs not claimed by any other feature (or another CS slot).</summary>
    private IEnumerable<byte> FreePins(int slot, bool adcOnly)
    {
        var owners = HardwarePins.BuildOwnerMap(_vm, excludeCsSlot: slot);
        var pool = adcOnly ? CsLimits.AdcPins : HardwarePins.ValidPins;
        foreach (var pin in pool)
            if (!owners.ContainsKey(pin)) yield return pin;
    }

    private IEnumerable<(int noun, CsNounDesc nd)> AvailableNouns(CsType type)
    {
        var caps = _vm.CsCaps!;
        var typeDesc = caps.DescFor(type);
        for (int n = 0; n < _vm.CsNounDescs.Count; n++)
        {
            var nd = _vm.CsNounDescs[n];
            if (nd == null || !nd.IsAvailable) continue;
            // Only nouns whose action set intersects this component's actions.
            if (typeDesc != null && (typeDesc.Value.Actions & nd.Actions) == 0) continue;
            yield return (n, nd);
        }
    }

    private IEnumerable<(int noun, CsNounDesc nd)> AvailableIrNouns()
    {
        // IR commands are button-shaped; use the button type's action mask.
        var caps = _vm.CsCaps!;
        var btn = caps.DescFor(CsType.Button);
        for (int n = 0; n < _vm.CsNounDescs.Count; n++)
        {
            var nd = _vm.CsNounDescs[n];
            if (nd == null || !nd.IsAvailable) continue;
            if (btn != null && (btn.Value.Actions & nd.Actions) == 0) continue;
            yield return (n, nd);
        }
    }

    private List<CsAction> ValidActions(CsType type, CsNounDesc nd)
    {
        var caps = _vm.CsCaps!;
        var typeDesc = caps.DescFor(type);
        var list = new List<CsAction>();
        foreach (CsAction a in Enum.GetValues<CsAction>())
            if ((typeDesc?.SupportsAction(a) ?? false) && nd.SupportsAction(a))
                list.Add(a);
        return list;
    }

    private List<CsAction> ValidIrActions(CsNounDesc nd)
    {
        // Button subset: INC, DEC, TOGGLE, SET, TRIGGER, MOMENTARY.
        var subset = new[] { CsAction.Inc, CsAction.Dec, CsAction.Toggle, CsAction.Set, CsAction.Trigger, CsAction.Momentary };
        return subset.Where(nd.SupportsAction).ToList();
    }

    private IEnumerable<(int band, string label)> BandOptions(int noun)
    {
        for (int b = 0; b < 10; b++) yield return (b, $"PEQ {b + 1}");
        if (noun == (int)CsNoun.FilterFreq || noun == (int)CsNoun.FilterBypass)
            for (int b = 20; b < 24; b++) yield return (b, $"Crossover {b - 19}");
    }

    private CsBinding MakeDefaultBinding(CsType type, int slot)
    {
        var b = new CsBinding { Type = type, Gpio1 = CsLimits.GpioUnused };
        var caps = _vm.CsCaps!;
        var typeDesc = caps.DescFor(type);
        bool adcOnly = typeDesc?.PinClass == CsPinClass.Adc;
        bool twoPins = typeDesc?.PinCount == 2;

        // Pins.
        var free = FreePins(slot, adcOnly).ToList();
        if (free.Count > 0) b.Gpio0 = free[0];
        if (twoPins) b.Gpio1 = free.Count > 1 ? free[1] : CsLimits.GpioUnused;

        if (type == CsType.Button) b.Event = (byte)CsEvent.Press;

        if (type == CsType.Ir)
        {
            // Container binding: noun/action must be 0.
            b.Noun = 0; b.Action = 0;
            return b;
        }

        // Default noun/action = first available intersecting the type.
        var noun = AvailableNouns(type).FirstOrDefault();
        if (noun.nd != null)
        {
            b.Noun = (byte)noun.noun;
            var acts = ValidActions(type, noun.nd);
            if (acts.Count > 0) b.Action = (byte)acts[0];
        }
        return b;
    }

    // ── Small view helpers ───────────────────────────────────────────────────

    private static Style? AccentStyle =>
        Application.Current.Resources.TryGetValue("AccentButtonStyle", out var s) ? s as Style : null;

    private static Brush SecondaryBrush =>
        Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var b) && b is Brush br
            ? br : new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));

    private static Grid Row(string label, FrameworkElement control)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lbl = new TextBlock
        {
            Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
        };
        Grid.SetColumn(lbl, 0);
        g.Children.Add(lbl);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    /// <summary>"Value (dB)" / "Value (ms)" …, or a bare "Value" for a unitless
    /// noun (or one whose unit this build doesn't know).</summary>
    private static string ValueLabel(CsUnit unit)
    {
        string sym = CsWire.UnitSymbol(unit);
        return sym.Length > 0 ? $"Value ({sym})" : "Value";
    }

    /// <summary>"Step (dB)" / "Step (ms)" …, bare "Step" for an unknown unit.</summary>
    private static string StepLabel(CsUnit unit)
    {
        string sym = CsWire.UnitSymbol(unit);
        return sym.Length > 0 ? $"Step ({sym})" : "Step";
    }

    /// <summary>A numeric text field that parses on Enter / focus loss and calls
    /// back with the parsed value. Unit is used only for display formatting.</summary>
    private static TextBox NumberField(double value, CsUnit unit, Action<double> onChanged)
    {
        var box = new TextBox { MinWidth = 100, Text = FormatNumber(value) };
        void Commit()
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                || double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
            {
                onChanged(v);
                box.Text = FormatNumber(v);
            }
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) Commit(); };
        return box;
    }

    private static string FormatNumber(double v) =>
        v == Math.Truncate(v)
            ? ((long)v).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Update a status pill's dot, label and tinted capsule in one go.</summary>
    private static void SetPill((Border Pill, Ellipse Dot, TextBlock Label) pill, string text, Color c)
    {
        pill.Label.Text = text;
        pill.Label.Foreground = new SolidColorBrush(c);
        pill.Dot.Fill = new SolidColorBrush(c);
        pill.Pill.Background = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B));
    }

    /// <summary>Header line for a remote-button card, e.g. "Volume (Output 1) · Increase".</summary>
    private string IrCommandTitle(int sub)
    {
        var d = _irDrafts[sub];
        var nd = _vm.CsNounDescFor(d.Noun);
        if (nd == null) return "Remote button";
        string s = CsNounInfo.Name(d.Noun);
        if (nd.IsTargeted) s += $" ({ChannelLabel(nd.TargetKind, d.Target)})";
        return $"{s} · {ActionName((CsAction)d.Action)}";
    }

    private void UpdateIrTitle(int sub)
    {
        if (_irTitles.TryGetValue(sub, out var t)) t.Text = IrCommandTitle(sub);
    }

    /// <summary>Card title: the user's custom name, falling back to the type name.</summary>
    private string SlotTitle(int slot) =>
        string.IsNullOrWhiteSpace(_nameEdits[slot]) ? TypeName(_drafts[slot].Type) : _nameEdits[slot].Trim();

    /// <summary>One-line binding summary shown under the card title, e.g.
    /// "Button · Volume · Output 1 · GPIO 5".</summary>
    private string SlotSummary(int slot)
    {
        var d = _drafts[slot];
        if (!d.IsConfigured) return "";
        var parts = new List<string>();
        // Lead with the type when a custom name has displaced it from the title.
        if (!string.IsNullOrWhiteSpace(_nameEdits[slot])) parts.Add(TypeName(d.Type));

        if (d.Type == CsType.Ir)
        {
            int n = ConfiguredIrCount();
            parts.Add(n == 1 ? "1 remote button" : $"{n} remote buttons");
        }
        else
        {
            var nd = _vm.CsNounDescFor(d.Noun);
            if (nd != null)
            {
                string noun = CsNounInfo.Name(d.Noun);
                if (nd.IsTargeted) noun += $" ({ChannelLabel(nd.TargetKind, d.Target)})";
                parts.Add(noun);
            }
        }
        if (d.Gpio0 != CsLimits.GpioUnused)
            parts.Add(d.Gpio1 != CsLimits.GpioUnused ? $"GPIO {d.Gpio0} + {d.Gpio1}" : $"GPIO {d.Gpio0}");
        return string.Join("  ·  ", parts);
    }

    private static string TypeGlyph(CsType t) => t switch
    {
        CsType.Button => "",   // power-button circle
        CsType.Switch => "",   // switch arrows
        CsType.Pot => "",      // dial / meter
        CsType.Encoder => "",  // rotate
        CsType.Led => "",      // lightbulb
        CsType.LedPwm => "",   // brightness
        CsType.Ir => "",       // remote
        _ => "",
    };

    /// <summary>Per-type badge tint, drawn from the app's channel-colour palette.</summary>
    private static Color TypeColor(CsType t) => t switch
    {
        CsType.Button => Color.FromArgb(255, 0x4A, 0x8F, 0xE3),  // blue
        CsType.Switch => Color.FromArgb(255, 0x45, 0xC2, 0xA3),  // teal
        CsType.Pot => Color.FromArgb(255, 0xF0, 0xC4, 0x59),     // amber
        CsType.Encoder => Color.FromArgb(255, 0xBA, 0x87, 0xF3), // purple
        CsType.Led => Color.FromArgb(255, 0x85, 0xC6, 0x62),     // green
        CsType.LedPwm => Color.FromArgb(255, 0x52, 0xB9, 0xD8),  // cyan
        CsType.Ir => Color.FromArgb(255, 0xF5, 0x73, 0x73),      // coral
        _ => Color.FromArgb(255, 0x90, 0x90, 0x90),
    };

    private static string TypeName(CsType t) => t switch
    {
        CsType.Button => "Button",
        CsType.Switch => "Switch",
        CsType.Pot => "Potentiometer",
        CsType.Encoder => "Rotary Encoder",
        CsType.Led => "LED",
        CsType.LedPwm => "LED (dimmable)",
        CsType.Ir => "IR Remote",
        _ => "None",
    };

    private static string ActionName(CsAction a) => a switch
    {
        CsAction.Adjust => "Adjust (absolute)",
        CsAction.Step => "Step per detent",
        CsAction.Inc => "Increase",
        CsAction.Dec => "Decrease",
        CsAction.Toggle => "Toggle",
        CsAction.Set => "Set value",
        CsAction.Follow => "Follow switch",
        CsAction.Trigger => "Trigger",
        CsAction.IndEquals => "Indicate (equals)",
        CsAction.Momentary => "Momentary (while held)",
        CsAction.IndAbove => "Indicate (above)",
        CsAction.IndLevel => "Indicate level",
        _ => a.ToString(),
    };

    /// <summary>Channel target label: the user-editable sidebar name, kept in sync
    /// by <see cref="RefreshChannelLabels"/> while this window is open.</summary>
    private string ChannelLabel(CsTarget kind, int i) => _vm.CsTargetLabel(kind, i);

    /// <summary>Rewrite a channel picker's item captions in place. Items and the
    /// current selection are untouched, so no SelectionChanged handler fires.</summary>
    private void Relabel(ComboBox combo, CsTarget kind)
    {
        foreach (var o in combo.Items)
            if (o is ComboBoxItem item && item.Tag is int i)
                item.Content = ChannelLabel(kind, i);
    }

    /// <summary>Re-read every channel name shown in this window after a sidebar
    /// rename: the target pickers, the IR command titles, and the card summaries
    /// (refreshed by <see cref="RefreshStatusIndicators"/>).</summary>
    private void RefreshChannelLabels()
    {
        foreach (var relabel in _channelRelabel.Values) relabel();
        foreach (var relabel in _irChannelRelabel.Values) relabel();
        foreach (int sub in _irTitles.Keys) UpdateIrTitle(sub);
        RefreshStatusIndicators();
    }
}
