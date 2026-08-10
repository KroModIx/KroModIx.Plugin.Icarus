using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace KroModIx.Plugin.Icarus.Views;

/// <summary>Downloads-Tab: Kroste-Card-Look, pro Row Install + Delete-Button.
/// Auto-Refresh im VM via FileSystemWatcher + DownloadEventBus.</summary>
public sealed class DownloadsView : UserControl
{
    public DownloadsView()
    {
        var openBtn = new Button { Content = "📂  Downloads-Ordner öffnen" };
        openBtn.Bind(Button.CommandProperty, new Binding(nameof(DownloadsViewModel.OpenDownloadsFolderCommand)));
        var refreshBtn = new Button { Content = "↺  Aktualisieren" };
        refreshBtn.Bind(Button.CommandProperty, new Binding(nameof(DownloadsViewModel.RefreshCommand)));

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Margin = new Thickness(0, 0, 0, 10),
            Children = { openBtn, refreshBtn },
        };

        var pathLabel = new TextBlock { FontSize = 11, Margin = new Thickness(0, 0, 0, 8) };
        pathLabel.Classes.Add("muted");
        pathLabel.Bind(TextBlock.TextProperty, new Binding(nameof(DownloadsViewModel.DownloadsDir))
        { StringFormat = "Ordner: {0}" });

        var summary = new TextBlock { Margin = new Thickness(0, 10, 0, 0) };
        summary.Classes.Add("muted");
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(DownloadsViewModel.Summary)));

        var list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            SelectionMode = SelectionMode.Single,
        };
        list.Bind(ListBox.ItemsSourceProperty, new Binding(nameof(DownloadsViewModel.Rows)));
        list.Bind(ListBox.SelectedItemProperty, new Binding(nameof(DownloadsViewModel.Selected))
        { Mode = BindingMode.TwoWay });
        list.ItemTemplate = new FuncDataTemplate<DownloadRow>((row, _) => row is null ? null : BuildRowTemplate(), true);

        Content = new DockPanel
        {
            Margin = new Thickness(20, 16, 20, 14),
            Children =
            {
                WithDock(toolbar, Dock.Top),
                WithDock(pathLabel, Dock.Top),
                WithDock(summary, Dock.Bottom),
                list,
            },
        };
    }

    private static Control BuildRowTemplate()
    {
        // Cover: gleiche 140×90-Landscape-Karte wie im Nexus-Tab (Nexus-CDN
        // liefert die Bilder in dem Format). Fallback: 📦-Emoji auf grauem
        // Grund wenn kein Cover geladen ist oder der Filename nicht dem
        // Nexus-Muster entspricht.
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
            Text = "📦", FontSize = 32,
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
        coverImage.Bind(Image.SourceProperty, new Binding(nameof(DownloadRow.Cover)));
        coverPanel.Children.Add(coverImage);
        coverFrame.Child = coverPanel;

        // Title = ModName wenn schon vom Nexus-Detail-Fetch da, sonst FileName
        // als Fallback (via DownloadRow.DisplayName).
        var title = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(DownloadRow.DisplayName)));

        // Meta-Zeile: Author · Version · Size · Datum
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0) };
        var authorTb = new TextBlock(); authorTb.Classes.Add("muted");
        authorTb.Bind(TextBlock.TextProperty, new Binding(nameof(DownloadRow.Author)));
        var sep1 = new TextBlock { Text = "·" }; sep1.Classes.Add("muted");
        var versionTb = new TextBlock(); versionTb.Classes.Add("muted");
        versionTb.Bind(TextBlock.TextProperty, new Binding(nameof(DownloadRow.Version)) { StringFormat = "v{0}" });
        var sep2 = new TextBlock { Text = "·" }; sep2.Classes.Add("muted");
        var size = new TextBlock(); size.Classes.Add("muted");
        size.Bind(TextBlock.TextProperty, new Binding(nameof(DownloadRow.Size)));
        var sep3 = new TextBlock { Text = "·" }; sep3.Classes.Add("muted");
        var dl = new TextBlock(); dl.Classes.Add("muted");
        dl.Bind(TextBlock.TextProperty, new Binding(nameof(DownloadRow.DownloadedText)));
        meta.Children.Add(authorTb); meta.Children.Add(sep1);
        meta.Children.Add(versionTb); meta.Children.Add(sep2);
        meta.Children.Add(size); meta.Children.Add(sep3); meta.Children.Add(dl);

        // Summary: 2 Zeilen, wird nur eingeblendet wenn Nexus-Detail-Fetch etwas geliefert hat.
        var summaryTb = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 40,
        };
        summaryTb.Classes.Add("secondary");
        summaryTb.Bind(TextBlock.TextProperty, new Binding(nameof(DownloadRow.Summary)));
        summaryTb.Bind(TextBlock.IsVisibleProperty, new Binding(nameof(DownloadRow.HasSummary)));

        // Ursprünglicher Datei-Name in kleiner Muted-Zeile — für Sanity/Debug.
        var fileNameTb = new TextBlock { FontSize = 10, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        fileNameTb.Classes.Add("muted");
        fileNameTb.Bind(TextBlock.TextProperty, new Binding(nameof(DownloadRow.FileName)));

        var textStack = new StackPanel
        {
            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            Children = { title, meta, summaryTb, fileNameTb },
        };

        var installBtn = new Button { Content = "📥  Installieren" };
        installBtn.Classes.Add("accent");
        BindRowCommand(installBtn, nameof(DownloadsViewModel.InstallRowCommand));

        var deleteBtn = new Button { Content = "🗑  Löschen" };
        deleteBtn.Classes.Add("danger");
        BindRowCommand(deleteBtn, nameof(DownloadsViewModel.DeleteRowCommand));

        var actions = new StackPanel
        {
            Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { installBtn, deleteBtn },
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

    private static void BindRowCommand(Button btn, string commandName)
    {
        btn.Bind(Button.CommandProperty, new Binding
        {
            RelativeSource = new RelativeSource { Mode = RelativeSourceMode.FindAncestor, AncestorType = typeof(ListBox) },
            Path = "DataContext." + commandName,
        });
        btn.Bind(Button.CommandParameterProperty, new Binding("."));
    }

    private static Control WithDock(Control c, Dock dock)
    {
        DockPanel.SetDock(c, dock);
        return c;
    }
}
