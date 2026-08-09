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
        var iconFrame = new Border
        {
            Width = 70, Height = 70,
            CornerRadius = new CornerRadius(6),
            [!Border.BackgroundProperty] = new DynamicResourceExtension("KrosteSurfaceBrush"),
        };
        var icon = new TextBlock
        {
            Text = "📦", FontSize = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Classes.Add("muted");
        iconFrame.Child = icon;

        var title = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(DownloadRow.FileName)));

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0) };
        var size = new TextBlock(); size.Classes.Add("muted");
        size.Bind(TextBlock.TextProperty, new Binding(nameof(DownloadRow.Size)));
        var sep = new TextBlock { Text = "·" }; sep.Classes.Add("muted");
        var dl = new TextBlock(); dl.Classes.Add("muted");
        dl.Bind(TextBlock.TextProperty, new Binding(nameof(DownloadRow.DownloadedText)));
        meta.Children.Add(size); meta.Children.Add(sep); meta.Children.Add(dl);

        var textStack = new StackPanel
        {
            Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            Children = { title, meta },
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
        Grid.SetColumn(iconFrame, 0);
        Grid.SetColumn(textStack, 1);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(iconFrame);
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
