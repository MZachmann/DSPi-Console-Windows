using System;
using System.Collections.Generic;
using System.Linq;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Settings;

/// <summary>
/// The Settings window's main layout host. Reads pages from
/// <see cref="SettingsRegistry"/>, builds a NavigationView with one
/// collapsible group per <see cref="SettingsCategory"/>, and swaps the
/// content area when the user picks a page.
///
/// <para>
/// Owns the window's <see cref="IPendingChangeTracker"/> and passes it
/// to each page's <c>BuildContent</c>. Subscribes to the tracker's
/// <see cref="IPendingChangeTracker.Changed"/> event to repaint the
/// sidebar pending-dots (per category + per page) and the top
/// pending-changes InfoBar.
/// </para>
///
/// <para>
/// Page content is built lazily on first navigation and cached for the
/// lifetime of the window. Initial page selection is deferred to the
/// Loaded event — selecting items during construction can race the
/// NavigationView's template application and trigger XAML failures.
/// </para>
/// </summary>
public sealed partial class SettingsShell : UserControl
{
    private readonly MainViewModel _vm;
    private readonly IPendingChangeTracker _tracker;
    private readonly Dictionary<string, UIElement> _pageCache = new();
    private readonly Dictionary<string, ISettingsPage> _pageById;

    // Per-nav-item handles for the pending-dot. Keyed by page id and by
    // category enum. The dot itself is a small Ellipse we hang off each
    // NavigationViewItem.Content via composition (Grid with text + dot).
    private readonly Dictionary<string, Microsoft.UI.Xaml.Shapes.Ellipse> _pageDots = new();
    private readonly Dictionary<SettingsCategory, Microsoft.UI.Xaml.Shapes.Ellipse> _categoryDots = new();

    private bool _initialSelectionDone;

    /// <summary>The shell's pending-change tracker. Exposed so the
    /// hosting <see cref="SettingsWindow"/> can hook close-confirm and
    /// show the top InfoBar.</summary>
    public IPendingChangeTracker Tracker => _tracker;

    public SettingsShell(MainViewModel vm, IPendingChangeTracker tracker)
    {
        try
        {
            _vm = vm;
            _tracker = tracker;
            InitializeComponent();

            _pageById = SettingsRegistry.Pages.ToDictionary(p => p.Id);

            BuildNavMenu();
            Nav.SelectionChanged += OnNavSelectionChanged;
            _tracker.Changed += OnTrackerChanged;

            Loaded += OnShellLoaded;
            Unloaded += (_, _) => _tracker.Changed -= OnTrackerChanged;
        }
        catch (Exception ex)
        {
            SettingsWindow.WriteCrashLog("SettingsShell ctor", ex);
            throw;
        }
    }

    private void OnShellLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialSelectionDone) return;
        _initialSelectionDone = true;
        try
        {
            // Default landing page is Hardware › Output Assignment — it's the
            // most-frequently-edited surface (per-output S/PDIF vs I²S +
            // GPIO mapping) and matches what users open Settings to do most
            // of the time. Fall back to the first registry-ordered available
            // page if the output-assignment page isn't registered or isn't
            // available (e.g. a future build that gates it on platform).
            const string DefaultPageId = "hardware.output-assignment";
            var landing = SettingsRegistry.Pages.FirstOrDefault(p =>
                              p.Id == DefaultPageId && p.IsAvailable(_vm))
                          ?? SettingsRegistry.Pages.FirstOrDefault(p =>
                              p.Category != SettingsCategory.About && p.IsAvailable(_vm));
            if (landing != null)
            {
                var item = FindMenuItem(landing.Id);
                if (item != null) Nav.SelectedItem = item;
            }
        }
        catch (Exception ex)
        {
            SettingsWindow.WriteCrashLog("SettingsShell.OnShellLoaded", ex);
            throw;
        }
    }

    /// <summary>
    /// Populate the NavigationView with one expandable parent per category
    /// (except About, which goes into FooterMenuItems). Each parent's
    /// children are the registry pages in that category, in Order. Pages
    /// whose <see cref="ISettingsPage.IsAvailable"/> returns false are
    /// elided — including the parent if the whole category empties out.
    /// </summary>
    private void BuildNavMenu()
    {
        var byCategory = SettingsRegistry.Pages
            .Where(p => p.IsAvailable(_vm))
            .GroupBy(p => p.Category)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Order).ToList());

        foreach (SettingsCategory cat in Enum.GetValues(typeof(SettingsCategory)))
        {
            if (cat == SettingsCategory.About) continue;
            if (!byCategory.TryGetValue(cat, out var pages) || pages.Count == 0) continue;

            var (title, glyph) = SettingsCategoryInfo.For(cat);

            var parent = new NavigationViewItem
            {
                Content = MakeNavContent(title, isCategory: true, out var catDot),
                Icon = new FontIcon { Glyph = glyph },
                SelectsOnInvoked = false,
                // Hardware starts expanded — it's the category that holds
                // the default landing page (Output Assignment, see
                // OnShellLoaded) and the most-visited group overall, so
                // collapsing it just makes the user click it open on every
                // Settings open. Other categories stay collapsed so the
                // nav tree doesn't fill the sidebar by default.
                IsExpanded = cat == SettingsCategory.Hardware,
            };
            _categoryDots[cat] = catDot;

            foreach (var page in pages)
            {
                parent.MenuItems.Add(new NavigationViewItem
                {
                    Content = MakeNavContent(page.Title, isCategory: false, out var pageDot),
                    Tag = page.Id,
                });
                _pageDots[page.Id] = pageDot;
            }

            Nav.MenuItems.Add(parent);
        }

        if (byCategory.TryGetValue(SettingsCategory.About, out var aboutPages))
        {
            var (aboutTitle, aboutGlyph) = SettingsCategoryInfo.For(SettingsCategory.About);
            foreach (var page in aboutPages.OrderBy(p => p.Order))
            {
                Nav.FooterMenuItems.Add(new NavigationViewItem
                {
                    Content = MakeNavContent(aboutTitle, isCategory: false, out _),
                    Tag = page.Id,
                    Icon = new FontIcon { Glyph = aboutGlyph },
                });
            }
        }
    }

    /// <summary>
    /// Build a Grid that's used as a NavigationViewItem's Content: a
    /// TextBlock with the page/category label, plus a small Ellipse on
    /// the right that we toggle visible when the tracker reports a
    /// pending change in this scope. The Ellipse is returned via the
    /// out parameter so we can repaint it later.
    /// </summary>
    private static Grid MakeNavContent(string label, bool isCategory, out Microsoft.UI.Xaml.Shapes.Ellipse dot)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = isCategory
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal,
        };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        // Accent-coloured pending dot. SystemAccentColor isn't a named
        // member of Microsoft.UI.Colors; pull the active theme brush
        // from app resources so the dot tracks Light/Dark + user accent
        // changes. Fallback to Microsoft's default Windows blue if the
        // resource isn't present for some reason.
        var fill = Application.Current.Resources.TryGetValue("SystemAccentColorBrush", out var brush)
            && brush is Microsoft.UI.Xaml.Media.Brush b
                ? b
                : (Microsoft.UI.Xaml.Media.Brush)new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 0, 120, 212));
        dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            Fill = fill,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(dot, 1);
        grid.Children.Add(dot);

        return grid;
    }

    /// <summary>Refresh the pending-dot visibility on every nav item
    /// and the top InfoBar's open/close state + message. Runs every
    /// time the tracker changes; cheap (10–20 ellipses + one InfoBar).</summary>
    private void OnTrackerChanged(object? sender, EventArgs e)
    {
        // Compute which pages and categories currently have pending edits.
        var pendingPages = new HashSet<string>();
        var pendingCategories = new HashSet<SettingsCategory>();
        foreach (var change in _tracker.Pending)
        {
            pendingPages.Add(change.PageId);
            if (_pageById.TryGetValue(change.PageId, out var page))
                pendingCategories.Add(page.Category);
        }

        foreach (var kvp in _pageDots)
            kvp.Value.Visibility = pendingPages.Contains(kvp.Key)
                ? Visibility.Visible : Visibility.Collapsed;

        foreach (var kvp in _categoryDots)
            kvp.Value.Visibility = pendingCategories.Contains(kvp.Key)
                ? Visibility.Visible : Visibility.Collapsed;

        // InfoBar: open when there are pending changes; message shows
        // count. Singular vs plural for the polished "1 pending change"
        // case so we don't read like a beta tool.
        var n = _tracker.Count;
        PendingBar.IsOpen = n > 0;
        PendingBar.Message = n == 1
            ? "1 pending device change"
            : $"{n} pending device changes";
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        _tracker.DiscardAll();
        // The Refresh path on each visible page needs to repaint to
        // reflect the un-staging — controls that show pending state
        // (inline (was X) chips) clear. Cheap: just re-Refresh the
        // currently-visible page; cached but-not-visible pages will
        // refresh on next navigation.
        RefreshVisiblePage();
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        ApplyButton.IsEnabled = false;
        DiscardButton.IsEnabled = false;
        try
        {
            var report = await _tracker.ApplyAllAsync();
            // If anything failed, leave the InfoBar visible (Changed
            // will fire again with the remaining count); on full
            // success the InfoBar closes itself. Failures are listed
            // in the InfoBar's Message via a Severity bump.
            if (report.Failed > 0)
            {
                PendingBar.Severity = InfoBarSeverity.Error;
                PendingBar.Message = report.Applied > 0
                    ? $"{report.Applied} applied, {report.Failed} failed"
                    : $"{report.Failed} failed";
            }
            else
            {
                // Restore default warning severity for the next batch.
                PendingBar.Severity = InfoBarSeverity.Warning;
            }
        }
        catch (Exception ex)
        {
            SettingsWindow.WriteCrashLog("SettingsShell.OnApplyClick", ex);
            PendingBar.Severity = InfoBarSeverity.Error;
            PendingBar.Message = $"Apply failed: {ex.Message}";
        }
        finally
        {
            ApplyButton.IsEnabled = true;
            DiscardButton.IsEnabled = true;
            RefreshVisiblePage();
        }
    }

    /// <summary>Re-Refresh the currently-displayed page so its UI
    /// reflects the post-tracker-change state (clears inline diff
    /// chips, etc.). No-op if no page is selected.</summary>
    private void RefreshVisiblePage()
    {
        if (PageHost.Content is SettingsModule mod)
            mod.RefreshFromShell();
    }

    private NavigationViewItem? FindMenuItem(string pageId)
    {
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if ((item.Tag as string) == pageId) return item;
            foreach (var child in item.MenuItems.OfType<NavigationViewItem>())
                if ((child.Tag as string) == pageId) return child;
        }
        foreach (var item in Nav.FooterMenuItems.OfType<NavigationViewItem>())
            if ((item.Tag as string) == pageId) return item;
        return null;
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        try
        {
            if (args.SelectedItem is not NavigationViewItem item) return;
            if (item.Tag is not string pageId) return;
            if (!_pageById.TryGetValue(pageId, out var page)) return;

            if (!_pageCache.TryGetValue(pageId, out var content))
            {
                content = page.BuildContent(_vm, _tracker);
                _pageCache[pageId] = content;
            }

            HeaderIcon.Glyph = page.IconGlyph;
            HeaderTitle.Text = page.Title;
            PageHost.Content = content;
        }
        catch (Exception ex)
        {
            SettingsWindow.WriteCrashLog("SettingsShell.OnNavSelectionChanged", ex);
        }
    }
}
