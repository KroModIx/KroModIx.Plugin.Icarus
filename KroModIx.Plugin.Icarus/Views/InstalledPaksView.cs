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
                WithDock(BuildUe4ssSection(), Dock.Top),
                WithDock(BuildExmodzSection(), Dock.Top),
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

        // WrapPanel, nicht StackPanel: ein StackPanel schneidet ab, was nicht
        // in die Breite passt — ohne Fehler und ohne Warnung. Bei zehn
        // Knoepfen mit deutschen Beschriftungen ist das keine theoretische
        // Gefahr (kroste-avalonia, Avalonia-12-Regel).
        var toolbar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 6,
            LineSpacing = 6,
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

        // v1.23.0: dritte Quelle. Ohne eigenen Umschalter waeren die
        // Lua-Mods von keinem der beiden anderen Filter erfasst und immer
        // sichtbar — inkonsistent zu PAK und Workshop.
        var luaToggle = new ToggleButton { Content = Strings.T("toggle.lua") };
        luaToggle.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(InstalledPaksViewModel.ShowLua))
        { Mode = BindingMode.TwoWay });

        var exmodzToggle = new ToggleButton { Content = Strings.T("toggle.exmodz") };
        exmodzToggle.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(InstalledPaksViewModel.ShowExmodz))
        { Mode = BindingMode.TwoWay });

        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        count.Classes.Add("muted");
        count.Bind(TextBlock.TextProperty, new Binding(nameof(InstalledPaksViewModel.SelectedCountLabel)));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetColumn(_searchBox!, 0);
        Grid.SetColumn(manualToggle, 1);
        Grid.SetColumn(workshopToggle, 2);
        Grid.SetColumn(luaToggle, 3);
        Grid.SetColumn(exmodzToggle, 4);
        Grid.SetColumn(count, 5);
        manualToggle.Margin = new Thickness(8, 0, 4, 0);
        workshopToggle.Margin = new Thickness(4, 0, 4, 0);
        luaToggle.Margin = new Thickness(4, 0, 4, 0);
        exmodzToggle.Margin = new Thickness(4, 0, 12, 0);
        grid.Children.Add(_searchBox!);
        grid.Children.Add(manualToggle);
        grid.Children.Add(workshopToggle);
        grid.Children.Add(luaToggle);
        grid.Children.Add(exmodzToggle);
        grid.Children.Add(count);
        return grid;
    }

    /// <summary>Der UE4SS-Abschnitt: eine Karte mit dem Zustand des
    /// Lua-Mod-Loaders und den Knöpfen, die ihn in Gang bringen.
    ///
    /// <para>Bewusst eine eigene Sektions-Karte und keine weiteren Knöpfe in
    /// der Werkzeugleiste. Der Grund ist nicht Ästhetik: der entscheidende
    /// Teil ist der <b>Zustandstext</b>. „Proton lädt seine eigene
    /// dwmapi.dll" erklärt dem User, warum seine Lua-Mods nichts tun —
    /// ein Knopf allein in der Leiste täte das nicht, und genau dieser
    /// Fehlerfall hat kein anderes Symptom als Wirkungslosigkeit.</para>
    ///
    /// <para>Die Karte verschwindet ganz, wenn
    /// <c>Icarus/Binaries/Win64</c> nicht gefunden wurde — dann ist nichts
    /// einzurichten.</para></summary>
    private static Control BuildUe4ssSection()
    {
        var heading = new TextBlock { Text = Strings.T("ue4ss.section") };
        heading.Classes.Add("section-label");

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(InstalledPaksViewModel.Ue4ssStatusText)));

        var proton = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        proton.Classes.Add("secondary");
        proton.Bind(TextBlock.TextProperty, new Binding(nameof(InstalledPaksViewModel.Ue4ssProtonText)));
        proton.Bind(TextBlock.IsVisibleProperty,
            new Binding(nameof(InstalledPaksViewModel.Ue4ssProtonText))
            {
                Converter = new Avalonia.Data.Converters.FuncValueConverter<string?, bool>(
                    v => !string.IsNullOrWhiteSpace(v)),
            });

        var loader = new TextBlock
        {
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        };
        loader.Classes.Add("muted");
        loader.Bind(TextBlock.TextProperty, new Binding(nameof(InstalledPaksViewModel.Ue4ssLoaderText)));
        loader.Bind(TextBlock.IsVisibleProperty,
            new Binding(nameof(InstalledPaksViewModel.Ue4ssLoaderText))
            {
                Converter = new Avalonia.Data.Converters.FuncValueConverter<string?, bool>(
                    v => !string.IsNullOrWhiteSpace(v)),
            });

        var installBtn = new Button { Content = Strings.T("ue4ss.btn_install") };
        installBtn.Classes.Add("accent");
        installBtn.Bind(Button.CommandProperty,
            new Binding(nameof(InstalledPaksViewModel.InstallUe4ssCommand)));
        // Beschriftung bleibt „installieren", auch wenn schon installiert —
        // derselbe Knopf ist dann der Update-Weg auf die neueste Ausgabe.
        installBtn.Bind(Button.IsVisibleProperty,
            new Binding(nameof(InstalledPaksViewModel.IsUe4ssSupported)));

        var protonBtn = new Button { Content = Strings.T("ue4ss.btn_fix_proton") };
        protonBtn.Bind(Button.CommandProperty,
            new Binding(nameof(InstalledPaksViewModel.FixProtonOverrideCommand)));
        protonBtn.Bind(Button.IsVisibleProperty,
            new Binding(nameof(InstalledPaksViewModel.NeedsProtonFix)));

        var openBtn = new Button { Content = Strings.T("ue4ss.btn_open_folder") };
        openBtn.Classes.Add("ghost");
        openBtn.Bind(Button.CommandProperty,
            new Binding(nameof(InstalledPaksViewModel.OpenUe4ssFolderCommand)));
        openBtn.Bind(Button.IsVisibleProperty,
            new Binding(nameof(InstalledPaksViewModel.IsUe4ssInstalled)));

        // WrapPanel auch hier — „UE4SS installieren" plus „DLL-Umleitung
        // setzen" plus „UE4SS-Ordner" wird in einem schmalen Fenster eng.
        var buttons = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 6, LineSpacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { installBtn, protonBtn, openBtn },
        };

        var texts = new StackPanel
        {
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { heading, status, proton, loader },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(texts, 0);
        Grid.SetColumn(buttons, 1);
        buttons.Margin = new Thickness(12, 0, 0, 0);
        grid.Children.Add(texts);
        grid.Children.Add(buttons);

        var card = new Border { Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 0, 0, 10) };
        card.Classes.Add("card");
        card.Child = grid;
        card.Bind(Border.IsVisibleProperty,
            new Binding(nameof(InstalledPaksViewModel.ShowUe4ssSection)));
        return card;
    }

    /// <summary>Der Datentabellen-Abschnitt: Zustand des gemeinsam gebauten
    /// Paks und der Neubau-Knopf.
    ///
    /// <para>Auch hier ist der <b>Zustandstext</b> der eigentliche Inhalt,
    /// nicht der Knopf. „Icarus wurde aktualisiert, das Pak steht auf dem
    /// alten Stand" ist eine Lage, die der User sonst nicht erkennen kann:
    /// die Mod liegt ordentlich in der Liste, das Pak liegt im Mods-Ordner,
    /// und im Spiel stimmen trotzdem Werte nicht.</para></summary>
    private static Control BuildExmodzSection()
    {
        var heading = new TextBlock { Text = Strings.T("exmodz.section") };
        heading.Classes.Add("section-label");

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(InstalledPaksViewModel.ExmodzStatusText)));

        // Der Hinweis auf ein fremdes Merged-Pak in Danger-Rot: er bedeutet,
        // dass unsere Aenderungen wirkungslos bleiben koennen.
        var conflict = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("KrosteDangerBrush"),
        };
        conflict.Bind(TextBlock.TextProperty,
            new Binding(nameof(InstalledPaksViewModel.ExmodzConflictText)));
        conflict.Bind(TextBlock.IsVisibleProperty,
            new Binding(nameof(InstalledPaksViewModel.HasExmodzConflict)));

        // Zwei Knöpfe statt einer mit wechselnder Style-Klasse: `Classes` ist
        // in Avalonia keine StyledProperty und lässt sich nicht binden. Der
        // Akzent soll aber nur leuchten, wenn ein Neubau wirklich fällig ist
        // — sonst wäre der auffälligste Knopf des Tabs dauerhaft laut und das
        // Signal wertlos.
        var rebuildUrgent = new Button { Content = Strings.T("exmodz.btn_rebuild") };
        rebuildUrgent.Classes.Add("accent");
        rebuildUrgent.Bind(Button.CommandProperty,
            new Binding(nameof(InstalledPaksViewModel.RebuildExmodzCommand)));
        rebuildUrgent.Bind(Button.IsVisibleProperty,
            new Binding(nameof(InstalledPaksViewModel.ExmodzNeedsRebuild)));

        var rebuildCalm = new Button { Content = Strings.T("exmodz.btn_rebuild") };
        rebuildCalm.Bind(Button.CommandProperty,
            new Binding(nameof(InstalledPaksViewModel.RebuildExmodzCommand)));
        rebuildCalm.Bind(Button.IsVisibleProperty,
            new Binding(nameof(InstalledPaksViewModel.ExmodzNeedsRebuild))
            {
                Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, bool>(v => !v),
            });

        var buttons = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 6, LineSpacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Children = { rebuildUrgent, rebuildCalm },
        };

        var texts = new StackPanel
        {
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { heading, status, conflict },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(texts, 0);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(texts);
        grid.Children.Add(buttons);

        var card = new Border { Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 0, 0, 10) };
        card.Classes.Add("card");
        card.Child = grid;
        card.Bind(Border.IsVisibleProperty,
            new Binding(nameof(InstalledPaksViewModel.ShowExmodzSection)));
        return card;
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
        // Fallback-Emoji nach Quelle: 🗻 Workshop, 🐍 Lua, 📦 manuelles PAK.
        // Lua-Mods haben nie ein Nexus-Cover (der Ordnername ist kein
        // Nexus-Dateiname), deshalb ist das Emoji dort der Dauerzustand und
        // nicht nur ein Platzhalter.
        coverFallback.Bind(TextBlock.TextProperty, new MultiBinding
        {
            Bindings =
            {
                new Binding(nameof(PakRow.IsWorkshop)),
                new Binding(nameof(PakRow.IsLua)),
                new Binding(nameof(PakRow.IsExmodz)),
            },
            Converter = new SourceEmojiConverter(),
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

        // Lua-Badge — damit auf einen Blick klar ist, dass diese Mod ueber
        // UE4SS laeuft und nicht ueber den Pak-Ordner. Beides zusammen in
        // einer Liste waere sonst verwirrend.
        var luaBadge = MakeBadge(Strings.T("badge.lua"), "KrosteAccentSoftBrush", Brushes.White);
        luaBadge.Bind(Border.IsVisibleProperty, new Binding(nameof(PakRow.IsLua)));
        titleRow.Children.Add(luaBadge);

        // Tabellen-Badge — eine Datentabellen-Mod liegt nicht als Datei im
        // Spiel, sondern wirkt ueber das gemeinsam gebaute Pak. Das muss in
        // der Row sichtbar sein, sonst sucht der User sie im Mods-Ordner.
        var exmodzBadge = MakeBadge(Strings.T("badge.exmodz"), "KrosteAccentSoftBrush", Brushes.White);
        exmodzBadge.Bind(Border.IsVisibleProperty, new Binding(nameof(PakRow.IsExmodz)));
        titleRow.Children.Add(exmodzBadge);

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
        // v1.23.0: bei Lua-Mods die Zahl der Skripte. Bei einem Ordner sagt
        // sie mehr ueber den Umfang als die Byte-Summe.
        var sep4 = new TextBlock { Text = "·" }; sep4.Classes.Add("muted");
        sep4.Bind(TextBlock.IsVisibleProperty, new Binding(nameof(PakRow.HasScriptsLabel)));
        var scriptsTb = new TextBlock(); scriptsTb.Classes.Add("muted");
        scriptsTb.Bind(TextBlock.TextProperty, new Binding(nameof(PakRow.ScriptsLabel)));
        scriptsTb.Bind(TextBlock.IsVisibleProperty, new Binding(nameof(PakRow.HasScriptsLabel)));

        meta.Children.Add(authorTb); meta.Children.Add(sep1);
        meta.Children.Add(versionTb); meta.Children.Add(sep2);
        meta.Children.Add(sizeTb); meta.Children.Add(sep3);
        meta.Children.Add(stateTb);
        meta.Children.Add(sep4); meta.Children.Add(scriptsTb);

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
        // Sichtbar bei allem, was nicht Workshop ist — bei Lua laeuft das
        // Umschalten ueber die enabled.txt, bei PAK ueber die Dateiendung.
        // Workshop bleibt read-only (Steam verwaltet die Ordner).
        toggleBtn.Bind(Button.IsVisibleProperty, new Binding(nameof(PakRow.IsWorkshop))
        {
            Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, bool>(v => !v),
        });

        var detailBtn = new Button { Content = Strings.T("btn.details") };
        BindRowCommand(detailBtn, nameof(InstalledPaksViewModel.ShowDetailCommand));
        detailBtn.Bind(Button.IsVisibleProperty, new Binding(nameof(PakRow.CanShowDetail)));
        ToolTip.SetTip(detailBtn, Strings.T("tooltip.details"));

        var uninstallBtn = new Button { Content = Strings.T("btn.uninstall") };
        uninstallBtn.Classes.Add("danger");
        BindRowCommand(uninstallBtn, nameof(InstalledPaksViewModel.UninstallRowCommand));
        uninstallBtn.Bind(Button.IsVisibleProperty, new Binding(nameof(PakRow.IsWorkshop))
        {
            Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, bool>(v => !v),
        });

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
            if (!HasModExtension(local)) continue;
            try { vm.InstallDroppedPak(local); count++; }
            catch { /* Notify läuft im VM */ }
        }
        if (count > 0) vm.RefreshCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>v1.23.0: auch Archive annehmen. Vorher liess sich ein von
    /// Nexus geladenes ZIP nicht in den Tab ziehen — das Ziehen wurde
    /// abgelehnt, ohne zu sagen warum.</summary>
    private static bool HasPakFiles(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return false;
        return files.Any(f => HasModExtension(f.Path.LocalPath));
    }

    /// <summary>Emoji nach Mod-Quelle. Eingabe ist
    /// [IsWorkshop, IsLua, IsExmodz]; was nichts davon ist, ist ein
    /// manuelles PAK.</summary>
    /// <summary>Endungs-Vorfilter fuer Drag&amp;Drop. Bewusst eine eigene
    /// kleine Liste und nicht der Archiv-Baukasten des Hosts: die View hat
    /// keinen Zugriff auf IHostServices, und beim Ziehen geht es nur darum,
    /// ob der Zeiger ueberhaupt etwas Plausibles haelt. Die verbindliche
    /// Einordnung macht danach der Installer am Inhalt.</summary>
    private static bool HasModExtension(string path)
        => path.EndsWith(".pak", System.StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".zip", System.StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".rar", System.StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".7z", System.StringComparison.OrdinalIgnoreCase);

    private sealed class SourceEmojiConverter : Avalonia.Data.Converters.IMultiValueConverter
    {
        public object? Convert(System.Collections.Generic.IList<object?> values,
            System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            var isWorkshop = values.Count > 0 && values[0] is bool w && w;
            var isLua = values.Count > 1 && values[1] is bool l && l;
            var isExmodz = values.Count > 2 && values[2] is bool x && x;
            if (isWorkshop) return "🗻";
            if (isLua) return "🐍";
            return isExmodz ? "🧩" : "📦";
        }
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
