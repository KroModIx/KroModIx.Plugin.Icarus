using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using KroModIx.Plugin.Icarus.Services;

namespace KroModIx.Plugin.Icarus.Views;

/// <summary>
/// Installiert-Tab im Kroste-Card-Look, analog zum LS25-InstalledModsView.
/// Zeigt manuelle Mods UND Steam-Workshop-Abos gemeinsam; Workshop-Rows sind
/// visuell markiert (WORKSHOP-Badge) und Toggle/Uninstall dort disabled.
/// Multi-Select via Ctrl/Shift + Klick, F5 = Refresh, Ctrl+F = Suche fokussieren,
/// Del = Bulk-Uninstall, Drag&amp;Drop von .pak-Files installiert direkt.
/// </summary>
public sealed class InstalledPaksView : UserControl
{
    private ListBox? _list;
    private TextBox? _searchBox;

    public InstalledPaksView()
    {
        Focusable = true;
        KeyDown += OnKeyDown;
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DragDrop.SetAllowDrop(this, true);

        _searchBox = BuildSearchBox();
        _list = BuildList();

        Content = new DockPanel
        {
            Margin = new Thickness(20, 16, 20, 14),
            Children =
            {
                WithDock(BuildToolbar(), Dock.Top),
                WithDock(BuildFilterRow(), Dock.Top),
                WithDock(BuildPathLabel(), Dock.Top),
                WithDock(BuildSummary(), Dock.Bottom),
                _list,
            },
        };
    }

    private static Control BuildToolbar()
    {
        var updateAllBtn = new Button { Name = "UpdateAllButton", Content = Strings.T("btn.update_all") };
        updateAllBtn.Classes.Add("accent");
        updateAllBtn.Bind(Button.CommandProperty, new Binding(nameof(InstalledPaksViewModel.UpdateAllCommand)));
        updateAllBtn.Bind(Button.IsEnabledProperty, new Binding(nameof(InstalledPaksViewModel.HasAnyUpdate)));
        ToolTip.SetTip(updateAllBtn, Strings.T("tooltip.update_all"));

        var checkUpdatesBtn = new Button { Name = "CheckUpdatesButton", Content = Strings.T("btn.check_updates") };
        checkUpdatesBtn.Bind(Button.CommandProperty, new Binding(nameof(InstalledPaksViewModel.CheckUpdatesCommand)));
        checkUpdatesBtn.Bind(Button.IsEnabledProperty, new Binding(nameof(InstalledPaksViewModel.IsCheckingUpdates))
        {
            Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, bool>(v => !v),
        });
        ToolTip.SetTip(checkUpdatesBtn, Strings.T("tooltip.check_updates"));

        var installBtn = new Button { Content = Strings.T("btn.install_pak") };
        installBtn.Bind(Button.CommandProperty, new Binding(nameof(InstalledPaksViewModel.InstallFromFileCommand)));
        var refreshBtn = new Button { Content = Strings.T("btn.refresh") };
        refreshBtn.Bind(Button.CommandProperty, new Binding(nameof(InstalledPaksViewModel.RefreshCommand)));

        var toggleBulkBtn = new Button { Content = Strings.T("btn.toggle_bulk") };
        toggleBulkBtn.Bind(Button.CommandProperty, new Binding(nameof(InstalledPaksViewModel.ToggleEnabledBulkCommand)));
        toggleBulkBtn.Bind(Button.IsEnabledProperty, new Binding(nameof(InstalledPaksViewModel.HasMultiSelection)));

        var uninstallBulkBtn = new Button { Content = Strings.T("btn.uninstall_selection") };
        uninstallBulkBtn.Classes.Add("danger");
        uninstallBulkBtn.Bind(Button.CommandProperty, new Binding(nameof(InstalledPaksViewModel.UninstallBulkCommand)));
        uninstallBulkBtn.Bind(Button.IsEnabledProperty, new Binding(nameof(InstalledPaksViewModel.HasMultiSelection)));

        var openManualBtn = new Button { Content = Strings.T("btn.open_mods_folder") };
        openManualBtn.Bind(Button.CommandProperty, new Binding(nameof(InstalledPaksViewModel.OpenModsFolderCommand)));
        var openWorkshopBtn = new Button { Content = Strings.T("btn.open_workshop_folder") };
        openWorkshopBtn.Bind(Button.CommandProperty, new Binding(nameof(InstalledPaksViewModel.OpenWorkshopFolderCommand)));
        var backupBtn = new Button { Content = Strings.T("btn.backup") };
        backupBtn.Bind(Button.CommandProperty, new Binding(nameof(InstalledPaksViewModel.CreateBackupCommand)));
        var restoreBtn = new Button { Content = Strings.T("btn.restore") };
        restoreBtn.Bind(Button.CommandProperty, new Binding(nameof(InstalledPaksViewModel.RestoreBackupCommand)));

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 10),
        };
        toolbar.Children.Add(checkUpdatesBtn);
        toolbar.Children.Add(updateAllBtn);
        toolbar.Children.Add(NewDivider());
        toolbar.Children.Add(installBtn);
        toolbar.Children.Add(refreshBtn);
        toolbar.Children.Add(toggleBulkBtn);
        toolbar.Children.Add(uninstallBulkBtn);
        toolbar.Children.Add(NewDivider());
        toolbar.Children.Add(openManualBtn);
        toolbar.Children.Add(openWorkshopBtn);
        toolbar.Children.Add(backupBtn);
        toolbar.Children.Add(restoreBtn);
        return toolbar;
    }

    private static TextBox BuildSearchBox()
    {
        var box = new TextBox
        {
            [!TextBox.PlaceholderTextProperty] = new Binding
            {
                Source = Strings.T("placeholder.filter_paks"),
            },
            Margin = new Thickness(0, 0, 8, 0),
        };
        box.Bind(TextBox.TextProperty, new Binding(nameof(InstalledPaksViewModel.SearchText))
        { Mode = BindingMode.TwoWay });
        return box;
    }

    private Control BuildFilterRow()
    {
        var manualToggle = new ToggleButton { Content = Strings.T("toggle.manual") };
        manualToggle.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(InstalledPaksViewModel.ShowManual))
        { Mode = BindingMode.TwoWay });

        var workshopToggle = new ToggleButton { Content = Strings.T("toggle.workshop") };
        workshopToggle.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(InstalledPaksViewModel.ShowWorkshop))
        { Mode = BindingMode.TwoWay });

        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        count.Classes.Add("muted");
        count.Bind(TextBlock.TextProperty, new Binding(nameof(InstalledPaksViewModel.SelectedCountLabel)));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetColumn(_searchBox!, 0);
        Grid.SetColumn(manualToggle, 1);
        Grid.SetColumn(workshopToggle, 2);
        Grid.SetColumn(count, 3);
        manualToggle.Margin = new Thickness(8, 0, 4, 0);
        workshopToggle.Margin = new Thickness(4, 0, 12, 0);
        grid.Children.Add(_searchBox!);
        grid.Children.Add(manualToggle);
        grid.Children.Add(workshopToggle);
        grid.Children.Add(count);
        return grid;
    }

    private static Control BuildPathLabel()
    {
        var text = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 8),
        };
        text.Classes.Add("muted");
        text.Bind(TextBlock.TextProperty, new Binding(nameof(InstalledPaksViewModel.ModsDir))
        { StringFormat = Strings.T("label.manual_path_prefix") });
        return text;
    }

    private static Control BuildSummary()
    {
        var summary = new TextBlock { Margin = new Thickness(0, 10, 0, 0) };
        summary.Classes.Add("muted");
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(InstalledPaksViewModel.Summary)));
        return summary;
    }

    private ListBox BuildList()
    {
        var list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };
        list.Bind(ListBox.ItemsSourceProperty, new Binding(nameof(InstalledPaksViewModel.Mods)));
        list.Bind(ListBox.SelectedItemProperty, new Binding(nameof(InstalledPaksViewModel.Selected))
        { Mode = BindingMode.TwoWay });

        list.SelectionChanged += (_, _) =>
        {
            if (DataContext is not InstalledPaksViewModel vm) return;
            vm.SelectedRows.Clear();
            foreach (var it in list.SelectedItems!)
                if (it is PakRow r) vm.SelectedRows.Add(r);
        };

        list.ItemTemplate = new FuncDataTemplate<PakRow>((row, _) => row is null ? null : BuildRowTemplate(),
            supportsRecycling: true);
        // Doppelklick auf Row öffnet Detail-Dialog (analog Nexus- + Downloads-Tab).
        list.DoubleTapped += (_, _) =>
        {
            if (DataContext is InstalledPaksViewModel vm && list.SelectedItem is PakRow row)
                vm.ShowDetailCommand.Execute(row);
        };
        return list;
    }

    private static Control BuildRowTemplate()
    {
        // Cover-Frame links — Nexus-CDN liefert 400×225-Landscape, gleiche
        // Size wie Nexus-Tab + Downloads-Tab (140×90). Fallback: 🗻-Emoji für
        // Workshop-Rows, 📦 für Manual-Rows ohne Nexus-Cover.
        var coverFrame = new Border
        {
            Width = 140, Height = 90,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            [!Border.BackgroundProperty] = new DynamicResourceExtension("KrosteSurfaceBrush"),
        };
        var coverPanel = new Panel();
        var coverFallback = new TextBlock
        {
            FontSize = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        coverFallback.Classes.Add("muted");
        // Fallback-Emoji abhängig von Source: 🗻 (Icarus) für Workshop, 📦 für Manual.
        coverFallback.Bind(TextBlock.TextProperty, new Binding(nameof(PakRow.IsWorkshop))
        {
            Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, string>(v => v ? "🗻" : "📦"),
        });
        coverPanel.Children.Add(coverFallback);
        var coverImage = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        coverImage.Bind(Image.SourceProperty, new Binding(nameof(PakRow.Cover)));
        coverPanel.Children.Add(coverImage);
        coverFrame.Child = coverPanel;

        // Titel-Zeile: DisplayName (Mod-Name wenn vom Nexus da, sonst FileName) + Badges
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var title = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(PakRow.DisplayName)));
        titleRow.Children.Add(title);

        // aktiv-Badge — nur bei manuellen aktiven Mods
        var enabledBadge = MakeBadge(Strings.T("badge.active"), "KrosteSuccessBrush", Brushes.White);
        enabledBadge.Bind(Border.IsVisibleProperty, new MultiBinding
        {
            Bindings =
            {
                new Binding(nameof(PakRow.IsEnabled)),
                new Binding(nameof(PakRow.IsManual)),
            },
            Converter = new AllTrueConverter(),
        });
        titleRow.Children.Add(enabledBadge);

        // Workshop-Badge — nur bei Workshop-Rows, Kroste-Gold-Farbe
        var workshopBadge = MakeBadge(Strings.T("badge.workshop"), "KrosteGoldBrush", Brushes.Black);
        workshopBadge.Bind(Border.IsVisibleProperty, new Binding(nameof(PakRow.IsWorkshop)));
        titleRow.Children.Add(workshopBadge);

        // Update-Badge (Kroste-Gold auf schwarz) — nur wenn CheckUpdatesAsync
        // ein neueres Version bei Nexus entdeckt hat.
        var updateBadge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 1),
            VerticalAlignment = VerticalAlignment.Center,
            [!Border.BackgroundProperty] = new DynamicResourceExtension("KrosteGoldBrush"),
        };
        var updateBadgeText = new TextBlock
        {
            FontSize = 10, FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.Black,
        };
        updateBadgeText.Bind(TextBlock.TextProperty, new Binding(nameof(PakRow.UpdateBadgeText)));
        updateBadge.Child = updateBadgeText;
        updateBadge.Bind(Border.IsVisibleProperty, new Binding(nameof(PakRow.HasUpdate)));
        titleRow.Children.Add(updateBadge);

        // Meta-Zeile: Author · Version · Size · State (analog Downloads-Tab)
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0) };
        var authorTb = new TextBlock(); authorTb.Classes.Add("muted");
        authorTb.Bind(TextBlock.TextProperty, new Binding(nameof(PakRow.Author)));
        var sep1 = new TextBlock { Text = "·" }; sep1.Classes.Add("muted");
        var versionTb = new TextBlock(); versionTb.Classes.Add("muted");
        versionTb.Bind(TextBlock.TextProperty, new Binding(nameof(PakRow.Version)) { StringFormat = "v{0}" });
        var sep2 = new TextBlock { Text = "·" }; sep2.Classes.Add("muted");
        var sizeTb = new TextBlock(); sizeTb.Classes.Add("muted");
        sizeTb.Bind(TextBlock.TextProperty, new Binding(nameof(PakRow.Size)));
        var sep3 = new TextBlock { Text = "·" }; sep3.Classes.Add("muted");
        var stateTb = new TextBlock(); stateTb.Classes.Add("muted");
        stateTb.Bind(TextBlock.TextProperty, new Binding(nameof(PakRow.StateLabel)));
        meta.Children.Add(authorTb); meta.Children.Add(sep1);
        meta.Children.Add(versionTb); meta.Children.Add(sep2);
        meta.Children.Add(sizeTb); meta.Children.Add(sep3);
        meta.Children.Add(stateTb);

        // Summary — nur sichtbar wenn Nexus-Detail-Fetch etwas geliefert hat.
        var summaryTb = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 40,
        };
        summaryTb.Classes.Add("secondary");
        summaryTb.Bind(TextBlock.TextProperty, new Binding(nameof(PakRow.Summary)));
        summaryTb.Bind(TextBlock.IsVisibleProperty, new Binding(nameof(PakRow.HasSummary)));

        // Ursprünglicher Datei-Name in kleiner Muted-Zeile — für Sanity/Debug
        // (analog Downloads-Tab). Zeigt was tatsächlich im mods-Ordner liegt.
        var fileNameTb = new TextBlock
        {
            FontSize = 10, Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        fileNameTb.Classes.Add("muted");
        fileNameTb.Bind(TextBlock.TextProperty, new Binding(nameof(PakRow.FileName)));

        var textStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            Children = { titleRow, meta, summaryTb, fileNameTb },
        };

        // Row-Aktionen rechts
        // Update-Button (Accent) nur bei Manual-Rows mit HasUpdate.
        var updateBtn = new Button { Content = Strings.T("btn.update") };
        updateBtn.Classes.Add("accent");
        BindRowCommand(updateBtn, nameof(InstalledPaksViewModel.UpdateModCommand));
        updateBtn.Bind(Button.IsVisibleProperty, new Binding(nameof(PakRow.HasUpdate)));

        var toggleBtn = new Button { Content = Strings.T("btn.toggle_enabled") };
        BindRowCommand(toggleBtn, nameof(InstalledPaksViewModel.ToggleEnabledRowCommand));
        toggleBtn.Bind(Button.IsVisibleProperty, new Binding(nameof(PakRow.IsManual)));

        var detailBtn = new Button { Content = Strings.T("btn.details") };
        BindRowCommand(detailBtn, nameof(InstalledPaksViewModel.ShowDetailCommand));
        detailBtn.Bind(Button.IsVisibleProperty, new Binding(nameof(PakRow.CanShowDetail)));
        ToolTip.SetTip(detailBtn, Strings.T("tooltip.details"));

        var uninstallBtn = new Button { Content = Strings.T("btn.uninstall") };
        uninstallBtn.Classes.Add("danger");
        BindRowCommand(uninstallBtn, nameof(InstalledPaksViewModel.UninstallRowCommand));
        uninstallBtn.Bind(Button.IsVisibleProperty, new Binding(nameof(PakRow.IsManual)));

        var workshopHint = new TextBlock
        {
            Text = Strings.T("row.steam_managed"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        workshopHint.Classes.Add("muted");
        workshopHint.Bind(TextBlock.IsVisibleProperty, new Binding(nameof(PakRow.IsWorkshop)));

        var actions = new StackPanel
        {
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { updateBtn, toggleBtn, detailBtn, uninstallBtn, workshopHint },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(coverFrame, 0);
        Grid.SetColumn(textStack, 1);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(coverFrame);
        grid.Children.Add(textStack);
        grid.Children.Add(actions);

        var card = new Border { Margin = new Thickness(0, 0, 0, 8), Child = grid };
        card.Classes.Add("card");
        card.Bind(Border.OpacityProperty, new Binding(nameof(PakRow.IsEnabled))
        {
            Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, double>(v => v ? 1.0 : 0.55),
        });

        var ctxMenu = new ContextMenu();
        var miToggle = new MenuItem { Header = Strings.T("btn.toggle_enabled") };
        BindRowCommand(miToggle, nameof(InstalledPaksViewModel.ToggleEnabledRowCommand));
        var miDetail = new MenuItem { Header = Strings.T("btn.details") };
        BindRowCommand(miDetail, nameof(InstalledPaksViewModel.ShowDetailCommand));
        var miUninstall = new MenuItem { Header = Strings.T("btn.uninstall") };
        BindRowCommand(miUninstall, nameof(InstalledPaksViewModel.UninstallRowCommand));
        ctxMenu.Items.Add(miToggle);
        ctxMenu.Items.Add(miDetail);
        ctxMenu.Items.Add(new Separator());
        ctxMenu.Items.Add(miUninstall);
        card.ContextMenu = ctxMenu;

        return card;
    }

    private static Border MakeBadge(string text, string brushKey, IBrush foreground)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 1),
            VerticalAlignment = VerticalAlignment.Center,
            [!Border.BackgroundProperty] = new DynamicResourceExtension(brushKey),
        };
        b.Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = foreground,
        };
        return b;
    }

    private static Rectangle NewDivider()
    {
        var r = new Rectangle();
        r.Classes.Add("divider-v");
        return r;
    }

    private static void BindRowCommand(Button btn, string commandName)
    {
        btn.Bind(Button.CommandProperty, new Binding
        {
            RelativeSource = new RelativeSource { Mode = RelativeSourceMode.FindAncestor, AncestorType = typeof(ListBox) },
            Path = "DataContext." + commandName,
        });
        btn.Bind(Button.CommandParameterProperty, new Binding("."));
    }

    private static void BindRowCommand(MenuItem item, string commandName)
    {
        item.Bind(MenuItem.CommandProperty, new Binding
        {
            RelativeSource = new RelativeSource { Mode = RelativeSourceMode.FindAncestor, AncestorType = typeof(ListBox) },
            Path = "DataContext." + commandName,
        });
        item.Bind(MenuItem.CommandParameterProperty, new Binding("."));
    }

    private static Control WithDock(Control c, Dock dock)
    {
        DockPanel.SetDock(c, dock);
        return c;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not InstalledPaksViewModel vm) return;
        if (e.Key == Key.F5)
        {
            vm.RefreshCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            _searchBox?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            if (vm.SelectedRows.Count > 1)
                vm.UninstallBulkCommand.Execute(null);
            else if (vm.Selected is not null)
                vm.UninstallRowCommand.Execute(vm.Selected);
            e.Handled = true;
        }
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasPakFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not InstalledPaksViewModel vm) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;
        int count = 0;
        foreach (var f in files)
        {
            var local = f.Path.LocalPath;
            if (!local.EndsWith(".pak", System.StringComparison.OrdinalIgnoreCase)) continue;
            try { vm.InstallDroppedPak(local); count++; }
            catch { /* Notify läuft im VM */ }
        }
        if (count > 0) vm.RefreshCommand.Execute(null);
        e.Handled = true;
    }

    private static bool HasPakFiles(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return false;
        return files.Any(f => f.Path.LocalPath.EndsWith(".pak", System.StringComparison.OrdinalIgnoreCase));
    }

    private sealed class AllTrueConverter : Avalonia.Data.Converters.IMultiValueConverter
    {
        public object? Convert(System.Collections.Generic.IList<object?> values,
            System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            foreach (var v in values) if (v is bool b && !b) return false;
            return true;
        }
    }
}
