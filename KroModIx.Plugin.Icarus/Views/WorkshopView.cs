using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using KroModIx.Plugin.Icarus.Services;

namespace KroModIx.Plugin.Icarus.Views;

/// <summary>Workshop-Tab (v1.17): listet Steam-Workshop-Abos fuer Icarus.
/// Kroste-Card-Look identisch zum Nexus-Tab — Cover / Titel + Meta /
/// Actions rechts. Read-only, Discovery via Host-Contract
/// <see cref="KroModIx.Plugin.Contracts.IHostServices.Workshop"/>.</summary>
public sealed class WorkshopView : UserControl
{
    public WorkshopView()
    {
        var refreshBtn = new Button { Content = Strings.T("btn.refresh") };
        refreshBtn.Bind(Button.CommandProperty, new Binding(nameof(WorkshopViewModel.LoadCommand)));

        var filter = new TextBox
        {
            PlaceholderText = Strings.T("workshop.filter_placeholder"),
            Width = 340,
        };
        filter.Bind(TextBox.TextProperty, new Binding(nameof(WorkshopViewModel.FilterText))
        {
            Mode = BindingMode.TwoWay,
        });

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 8),
            Children = { filter, refreshBtn },
        };

        var status = new TextBlock { Margin = new Thickness(0, 0, 0, 8) };
        status.Classes.Add("muted");
        status.Bind(TextBlock.TextProperty, new Binding(nameof(WorkshopViewModel.StatusText)));

        var list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            SelectionMode = SelectionMode.Single,
        };
        list.Bind(ListBox.ItemsSourceProperty, new Binding(nameof(WorkshopViewModel.Rows)));
        list.ItemTemplate = new FuncDataTemplate<WorkshopRow>((row, _) =>
            row is null ? null : BuildRowCard(), true);

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        };

        Content = new DockPanel
        {
            Margin = new Thickness(16, 12),
            Children =
            {
                WithDock(toolbar, Dock.Top),
                WithDock(status, Dock.Top),
                scroll,
            },
        };
    }

    private static Control BuildRowCard()
    {
        var coverFrame = new Border
        {
            Width = 140,
            Height = 90,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            [!Border.BackgroundProperty] = new DynamicResourceExtension("KrosteSurfaceBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var coverPanel = new Panel();
        var coverFallback = new TextBlock
        {
            Text = "\U0001F310", // 🌐
            FontSize = 32,
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
        coverImage.Bind(Image.SourceProperty, new Binding(nameof(WorkshopRow.Cover)));
        coverPanel.Children.Add(coverImage);
        coverFrame.Child = coverPanel;

        var title = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(WorkshopRow.DisplayTitle)));

        var subtitle = new TextBlock { FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        subtitle.Classes.Add("muted");
        subtitle.Bind(TextBlock.TextProperty, new Binding(nameof(WorkshopRow.SubtitleText)));

        var description = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 40,
        };
        description.Classes.Add("secondary");
        description.Bind(TextBlock.TextProperty, new Binding(nameof(WorkshopRow.Description)));
        description.Bind(TextBlock.IsVisibleProperty, new Binding(nameof(WorkshopRow.HasDescription)));

        var titleColumn = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            Children = { title, subtitle, description },
        };

        var steamBtn = new Button { Content = Strings.T("workshop.btn_open_steam") };
        steamBtn.Classes.Add("accent");
        BindRowCommand(steamBtn, nameof(WorkshopViewModel.OpenInSteamCommand));

        var browserBtn = new Button { Content = Strings.T("workshop.btn_open_browser") };
        BindRowCommand(browserBtn, nameof(WorkshopViewModel.OpenInBrowserCommand));

        var folderBtn = new Button { Content = Strings.T("workshop.btn_open_folder") };
        folderBtn.Classes.Add("ghost");
        BindRowCommand(folderBtn, nameof(WorkshopViewModel.OpenFolderCommand));

        var actions = new StackPanel
        {
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { steamBtn, browserBtn, folderBtn },
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(12, 8),
        };
        Grid.SetColumn(coverFrame, 0);
        Grid.SetColumn(titleColumn, 1);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(coverFrame);
        grid.Children.Add(titleColumn);
        grid.Children.Add(actions);

        var card = new Border { Margin = new Thickness(0, 0, 0, 6), Child = grid };
        card.Classes.Add("card");
        return card;
    }

    private static void BindRowCommand(Button btn, string commandName)
    {
        btn.Bind(Button.CommandProperty, new Binding
        {
            RelativeSource = new RelativeSource
            { Mode = RelativeSourceMode.FindAncestor, AncestorType = typeof(ListBox) },
            Path = "DataContext." + commandName,
        });
        btn.Bind(Button.CommandParameterProperty, new Binding("."));
    }

    private static Control WithDock(Control c, Dock d) { DockPanel.SetDock(c, d); return c; }
}
