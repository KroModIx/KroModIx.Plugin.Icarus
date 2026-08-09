using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace KroModIx.Plugin.Icarus.Views;

/// <summary>
/// Nexus-Katalog-Tab. Zeigt die aggregierten Nexus-Listen (latest_added,
/// latest_updated, trending) im Kroste-Card-Look. Kein In-App-Download —
/// Klick auf „Nexus öffnen" führt zum Browser, User lädt aus Nexus in den
/// Plugin-Downloads-Ordner, der Downloads-Tab bietet dann Install-Buttons.
///
/// <para>Ohne API-Key: großes Info-Panel mit Hinweis auf den Settings-Tab.</para>
/// </summary>
public sealed class NexusView : UserControl
{
    public NexusView()
    {
        // Info-Panel wenn kein API-Key konfiguriert ist.
        var noKeyPanel = new Border
        {
            Padding = new Thickness(20),
            Margin = new Thickness(20),
            [!Border.BackgroundProperty] = new DynamicResourceExtension("KrosteSurfaceBrush"),
            CornerRadius = new CornerRadius(8),
        };
        var noKeyStack = new StackPanel { Spacing = 10 };
        var noKeyTitle = new TextBlock
        {
            Text = "Nexus-Mods braucht einen API-Key",
            FontSize = 16, FontWeight = FontWeight.SemiBold,
        };
        noKeyStack.Children.Add(noKeyTitle);
        var noKeyBody = new TextBlock
        {
            Text = "Öffne den Tab \"Nexus-Einstellungen\", trag deinen persönlichen "
                + "API-Key ein (kostenlos nach Nexus-Registrierung auf nexusmods.com "
                + "→ Account → API Keys) und komm dann hierher zurück.",
            TextWrapping = TextWrapping.Wrap,
        };
        noKeyBody.Classes.Add("muted");
        noKeyStack.Children.Add(noKeyBody);
        noKeyPanel.Child = noKeyStack;
        noKeyPanel.Bind(Border.IsVisibleProperty, new Binding(nameof(NexusViewModel.NeedsApiKey)));

        // Katalog-Panel — sichtbar wenn API-Key da ist.
        var catalogPanel = new DockPanel
        {
            Margin = new Thickness(20, 16, 20, 14),
        };
        catalogPanel.Bind(Control.IsVisibleProperty, new Binding(nameof(NexusViewModel.NeedsApiKey))
        {
            Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, bool>(v => !v),
        });

        // Toolbar
        var refreshBtn = new Button { Content = "↺  Aktualisieren" };
        refreshBtn.Bind(Button.CommandProperty, new Binding(nameof(NexusViewModel.RefreshCommand)));
        var openDownloadsBtn = new Button { Content = "📂  Downloads-Ordner" };
        openDownloadsBtn.Bind(Button.CommandProperty, new Binding(nameof(NexusViewModel.OpenDownloadsFolderCommand)));

        var searchBox = new TextBox
        {
            [!TextBox.PlaceholderTextProperty] = new Binding { Source = "Nexus-Katalog filtern …" },
        };
        searchBox.Bind(TextBox.TextProperty, new Binding(nameof(NexusViewModel.SearchText))
        { Mode = BindingMode.TwoWay });

        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetColumn(refreshBtn, 0);
        Grid.SetColumn(openDownloadsBtn, 1);
        Grid.SetColumn(searchBox, 2);
        refreshBtn.Margin = new Thickness(0, 0, 6, 0);
        openDownloadsBtn.Margin = new Thickness(0, 0, 12, 0);
        toolbar.Children.Add(refreshBtn);
        toolbar.Children.Add(openDownloadsBtn);
        toolbar.Children.Add(searchBox);
        DockPanel.SetDock(toolbar, Dock.Top);
        catalogPanel.Children.Add(toolbar);

        // Status + Busy-Anzeige unten
        var status = new TextBlock { Margin = new Thickness(0, 10, 0, 0) };
        status.Classes.Add("muted");
        status.Bind(TextBlock.TextProperty, new Binding(nameof(NexusViewModel.Status)));
        DockPanel.SetDock(status, Dock.Bottom);
        catalogPanel.Children.Add(status);

        // Liste
        var list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            SelectionMode = SelectionMode.Single,
        };
        list.Bind(ListBox.ItemsSourceProperty, new Binding(nameof(NexusViewModel.Rows)));
        list.ItemTemplate = new FuncDataTemplate<NexusRow>((row, _) => row is null ? null : BuildRowTemplate(), true);
        // Doppelklick auf Row öffnet Detail-Dialog (analog LS25-ModHubView).
        list.DoubleTapped += (_, _) =>
        {
            if (DataContext is NexusViewModel vm && list.SelectedItem is NexusRow row)
                vm.ShowDetailCommand.Execute(row);
        };
        catalogPanel.Children.Add(list);

        var root = new Panel();
        root.Children.Add(catalogPanel);
        root.Children.Add(noKeyPanel);
        Content = root;
    }

    private static Control BuildRowTemplate()
    {
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
            Text = "🗻", FontSize = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        coverFallback.Classes.Add("muted");
        coverPanel.Children.Add(coverFallback);
        var coverImage = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        coverImage.Bind(Image.SourceProperty, new Binding(nameof(NexusRow.Cover)));
        coverPanel.Children.Add(coverImage);
        coverFrame.Child = coverPanel;

        var title = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(NexusRow.Name)));

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0) };
        void AddMuted(Binding b, string? fmt = null)
        {
            var t = new TextBlock(); t.Classes.Add("muted");
            if (fmt is not null) b.StringFormat = fmt;
            t.Bind(TextBlock.TextProperty, b);
            meta.Children.Add(t);
        }
        AddMuted(new Binding(nameof(NexusRow.Author)));
        var sep1 = new TextBlock { Text = "·" }; sep1.Classes.Add("muted"); meta.Children.Add(sep1);
        AddMuted(new Binding(nameof(NexusRow.VersionDisplay)));
        var sep2 = new TextBlock { Text = "·" }; sep2.Classes.Add("muted"); meta.Children.Add(sep2);
        AddMuted(new Binding(nameof(NexusRow.UpdatedText)));
        var sep3 = new TextBlock { Text = "·" }; sep3.Classes.Add("muted"); meta.Children.Add(sep3);
        AddMuted(new Binding(nameof(NexusRow.EndorsementsText)));

        var summary = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 40,
        };
        summary.Classes.Add("secondary");
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(NexusRow.Summary)));

        var textStack = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            Children = { title, meta, summary },
        };

        // Premium-Download-Button — nur enabled wenn NexusViewModel.IsPremium.
        // Nexus liefert Direct-URLs nur für Premium-Konten; Free-User müssen
        // in den Browser (den „↗ Nexus öffnen"-Button darunter).
        var downloadBtn = new Button { Content = "⬇  Download" };
        downloadBtn.Classes.Add("accent");
        downloadBtn.Bind(Button.CommandProperty, new Binding
        {
            RelativeSource = new RelativeSource { Mode = RelativeSourceMode.FindAncestor, AncestorType = typeof(ListBox) },
            Path = "DataContext." + nameof(NexusViewModel.DownloadRowCommand),
        });
        downloadBtn.Bind(Button.CommandParameterProperty, new Binding("."));
        downloadBtn.Bind(Button.IsEnabledProperty, new Binding
        {
            RelativeSource = new RelativeSource { Mode = RelativeSourceMode.FindAncestor, AncestorType = typeof(ListBox) },
            Path = "DataContext." + nameof(NexusViewModel.IsPremium),
        });
        ToolTip.SetTip(downloadBtn, "Direct-Download in den Downloads-Ordner (Nexus-Premium nötig)");

        var detailBtn = new Button { Content = "🔍  Details" };
        detailBtn.Bind(Button.CommandProperty, new Binding
        {
            RelativeSource = new RelativeSource { Mode = RelativeSourceMode.FindAncestor, AncestorType = typeof(ListBox) },
            Path = "DataContext." + nameof(NexusViewModel.ShowDetailCommand),
        });
        detailBtn.Bind(Button.CommandParameterProperty, new Binding("."));

        var openBtn = new Button { Content = "↗  Nexus öffnen" };
        openBtn.Classes.Add("ghost");
        openBtn.Bind(Button.CommandProperty, new Binding
        {
            RelativeSource = new RelativeSource { Mode = RelativeSourceMode.FindAncestor, AncestorType = typeof(ListBox) },
            Path = "DataContext." + nameof(NexusViewModel.OpenRowInBrowserCommand),
        });
        openBtn.Bind(Button.CommandParameterProperty, new Binding("."));

        var actions = new StackPanel
        {
            Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { downloadBtn, detailBtn, openBtn },
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
        return card;
    }
}
