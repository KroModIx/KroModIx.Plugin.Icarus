using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace ModManager.Plugins.Icarus.Views;

/// <summary>Detail-Fenster für einen Nexus-Mod. Custom-Chrome (Kroste-Standard:
/// BorderOnly + ExtendClientAreaToDecorationsHint), Drag per Titelleiste,
/// Close-Button. Layout: großes Cover links, Titel + Meta + Beschreibung
/// rechts, KI-Zusammenfassung optional (auf Knopfdruck), Footer mit
/// „Nexus öffnen" und „Schließen".</summary>
public sealed class NexusModDetailWindow : Window
{
    public NexusModDetailWindow()
    {
        Title = "Nexus-Mod-Detail";
        Width = 900;
        Height = 720;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!Window.BackgroundProperty] = new DynamicResourceExtension("KrosteBackgroundBrush");
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        CanResize = true;

        Content = BuildContent();
    }

    private DockPanel BuildContent()
    {
        var titlebar = BuildTitleBar();
        var footer = BuildFooter();
        var body = BuildBody();

        var dp = new DockPanel();
        DockPanel.SetDock(titlebar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dp.Children.Add(titlebar);
        dp.Children.Add(footer);
        dp.Children.Add(body);
        return dp;
    }

    private Border BuildTitleBar()
    {
        var titleBlock = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        };
        titleBlock.Bind(TextBlock.TextProperty, new Binding(nameof(NexusModDetailViewModel.Title)));

        var closeBtn = new Button
        {
            Content = "✕",
            Width = 40, Height = 32,
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        closeBtn.Classes.Add("chrome");
        closeBtn.Click += (_, _) => Close();

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Height = 32,
        };
        Grid.SetColumn(titleBlock, 0);
        Grid.SetColumn(closeBtn, 1);
        grid.Children.Add(titleBlock);
        grid.Children.Add(closeBtn);

        var bar = new Border { Child = grid };
        bar[!Border.BackgroundProperty] = new DynamicResourceExtension("KrosteSurfaceBrush");
        bar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
        return bar;
    }

    private Control BuildBody()
    {
        // Cover links (240x160 — Nexus liefert Portrait 400x225 typischerweise).
        var coverFrame = new Border
        {
            Width = 240, Height = 160,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            [!Border.BackgroundProperty] = new DynamicResourceExtension("KrosteSurfaceBrush"),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var coverPanel = new Panel();
        var coverFallback = new TextBlock
        {
            Text = "🗻", FontSize = 48,
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
        coverImage.Bind(Image.SourceProperty, new Binding(nameof(NexusModDetailViewModel.Cover)));
        coverPanel.Children.Add(coverImage);
        coverFrame.Child = coverPanel;

        // Titel + Meta oben rechts
        var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
        title.Classes.Add("h1");
        title.Bind(TextBlock.TextProperty, new Binding(nameof(NexusModDetailViewModel.Title)));

        var adultBadge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            [!Border.BackgroundProperty] = new DynamicResourceExtension("KrosteDangerBrush"),
        };
        adultBadge.Child = new TextBlock
        {
            Text = "🔞 ADULT", FontSize = 10, FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
        };
        adultBadge.Bind(Border.IsVisibleProperty,
            new Binding(nameof(NexusModDetailViewModel.ContainsAdultContent)));

        var metaGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(0, 10, 0, 0),
        };
        AddMetaRow(metaGrid, 0, "Autor",         nameof(NexusModDetailViewModel.Author));
        AddMetaRow(metaGrid, 1, "Version",       nameof(NexusModDetailViewModel.Version));
        AddMetaRow(metaGrid, 2, "Kategorie",     nameof(NexusModDetailViewModel.Category));
        AddMetaRow(metaGrid, 3, "Aktualisiert",  nameof(NexusModDetailViewModel.UpdatedText));
        AddMetaRow(metaGrid, 4, "Endorsements",  nameof(NexusModDetailViewModel.EndorsementsText));

        var summary = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        summary.Classes.Add("secondary");
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(NexusModDetailViewModel.Summary)));

        var topRight = new StackPanel
        {
            Margin = new Thickness(16, 0, 0, 0),
            Children = { title, adultBadge, metaGrid, summary },
        };

        var topRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 0, 0, 16),
        };
        Grid.SetColumn(coverFrame, 0);
        Grid.SetColumn(topRight, 1);
        topRow.Children.Add(coverFrame);
        topRow.Children.Add(topRight);

        // KI-Zusammenfassungs-Bereich (nur sichtbar wenn HasSummary)
        var aiCard = BuildAiSummaryCard();

        // Beschreibung
        var descTitle = new TextBlock { Text = "Beschreibung", Margin = new Thickness(0, 8, 0, 6) };
        descTitle.Classes.Add("section-label");
        var desc = new TextBlock { TextWrapping = TextWrapping.Wrap };
        desc.Bind(TextBlock.TextProperty, new Binding(nameof(NexusModDetailViewModel.Description)));

        var descCard = new Border
        {
            Padding = new Thickness(14),
            [!Border.BackgroundProperty] = new DynamicResourceExtension("KrosteSurfaceBrush"),
            CornerRadius = new CornerRadius(8),
            Child = desc,
        };

        var scrollContent = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(20, 14, 20, 14),
            Children = { topRow, aiCard, descTitle, descCard },
        };

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = scrollContent,
        };
        return scroll;
    }

    private static Control BuildAiSummaryCard()
    {
        var title = new TextBlock { Text = "🤖 KI-Zusammenfassung", Margin = new Thickness(0, 0, 0, 6) };
        title.Classes.Add("section-label");
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap };
        body.Bind(TextBlock.TextProperty, new Binding(nameof(NexusModDetailViewModel.AiSummary)));
        var card = new Border
        {
            Padding = new Thickness(14),
            [!Border.BackgroundProperty] = new DynamicResourceExtension("KrosteAccentSoftBrush"),
            CornerRadius = new CornerRadius(8),
            Child = new StackPanel { Children = { title, body } },
        };
        card.Bind(Control.IsVisibleProperty, new Binding(nameof(NexusModDetailViewModel.HasSummary)));
        return card;
    }

    private Control BuildFooter()
    {
        // Primär-Aktion: Direct-Download (Premium). Ist disabled wenn kein
        // Premium-Konto — dann führt „Auf Nexus öffnen" zum Browser-Wall.
        var downloadBtn = new Button { Content = "⬇  Herunterladen" };
        downloadBtn.Classes.Add("accent");
        downloadBtn.Bind(Button.CommandProperty, new Binding(nameof(NexusModDetailViewModel.DownloadCommand)));
        downloadBtn.Bind(Button.IsEnabledProperty, new MultiBinding
        {
            Bindings =
            {
                new Binding(nameof(NexusModDetailViewModel.IsPremium)),
                new Binding(nameof(NexusModDetailViewModel.DownloadBusy)) { Converter = new NegateConverter() },
            },
            Converter = new AllTrueConverter(),
        });
        ToolTip.SetTip(downloadBtn,
            "Direct-Download in den Downloads-Ordner (Nexus-Premium nötig — sonst \"Auf Nexus öffnen\" für Browser)");

        var openBtn = new Button { Content = "↗  Auf Nexus öffnen" };
        openBtn.Bind(Button.CommandProperty, new Binding(nameof(NexusModDetailViewModel.OpenInBrowserCommand)));

        var summarizeBtn = new Button { Content = "🤖  KI-Zusammenfassung" };
        summarizeBtn.Bind(Button.CommandProperty, new Binding(nameof(NexusModDetailViewModel.SummarizeCommand)));
        summarizeBtn.Bind(Button.IsEnabledProperty, new Binding(nameof(NexusModDetailViewModel.SummaryBusy))
        {
            Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, bool>(v => !v),
        });

        var closeBtn = new Button { Content = "Schließen" };
        closeBtn.Classes.Add("ghost");
        closeBtn.Click += (_, _) => Close();

        var busy = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        busy.Classes.Add("muted");
        busy.Bind(TextBlock.TextProperty, new Binding(nameof(NexusModDetailViewModel.DownloadBusy))
        {
            Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, string>(v => v ? "…Download läuft…" : "…KI läuft…"),
        });
        busy.Bind(TextBlock.IsVisibleProperty, new MultiBinding
        {
            Bindings =
            {
                new Binding(nameof(NexusModDetailViewModel.SummaryBusy)),
                new Binding(nameof(NexusModDetailViewModel.DownloadBusy)),
            },
            Converter = new AnyTrueConverter(),
        });

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { busy, summarizeBtn, downloadBtn, openBtn, closeBtn },
        };

        var bar = new Border
        {
            Padding = new Thickness(14, 10),
            [!Border.BackgroundProperty] = new DynamicResourceExtension("KrosteSurfaceBrush"),
            Child = row,
        };
        return bar;
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

    private sealed class AnyTrueConverter : Avalonia.Data.Converters.IMultiValueConverter
    {
        public object? Convert(System.Collections.Generic.IList<object?> values,
            System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            foreach (var v in values) if (v is bool b && b) return true;
            return false;
        }
    }

    private sealed class NegateConverter : Avalonia.Data.Converters.IValueConverter
    {
        public object? Convert(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => value is bool b ? !b : Avalonia.Data.BindingOperations.DoNothing;
        public object? ConvertBack(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => value is bool b ? !b : Avalonia.Data.BindingOperations.DoNothing;
    }

    private static void AddMetaRow(Grid grid, int row, string label, string bindingPath)
    {
        var l = new TextBlock
        {
            Text = label, Margin = new Thickness(0, 2, 10, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        l.Classes.Add("muted");
        var v = new TextBlock { Margin = new Thickness(0, 2, 0, 2), VerticalAlignment = VerticalAlignment.Center };
        v.Bind(TextBlock.TextProperty, new Binding(bindingPath));
        Grid.SetRow(l, row); Grid.SetColumn(l, 0);
        Grid.SetRow(v, row); Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);
    }
}
