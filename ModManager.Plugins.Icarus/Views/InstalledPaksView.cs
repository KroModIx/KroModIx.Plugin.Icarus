using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace ModManager.Plugins.Icarus.Views;

public sealed class InstalledPaksView : UserControl
{
    public InstalledPaksView()
    {
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 12),
        };
        var install = new Button { Content = "📁  PAK installieren…" };
        install.Bind(Button.CommandProperty, new Binding(nameof(InstalledPaksViewModel.InstallFromFileCommand)));
        var refresh = new Button { Content = "🔄  Refresh" };
        refresh.Bind(Button.CommandProperty, new Binding(nameof(InstalledPaksViewModel.RefreshCommand)));
        var openFolder = new Button { Content = "📂  Mods-Ordner öffnen" };
        openFolder.Bind(Button.CommandProperty, new Binding(nameof(InstalledPaksViewModel.OpenModsFolderCommand)));
        var toggle = new Button { Content = "🔀  Aktiv/Inaktiv umschalten" };
        toggle.Bind(Button.CommandProperty, new Binding(nameof(InstalledPaksViewModel.ToggleEnabledCommand)));
        var uninstall = new Button { Content = "🗑  Deinstallieren" };
        uninstall.Classes.Add("danger");
        uninstall.Bind(Button.CommandProperty, new Binding(nameof(InstalledPaksViewModel.UninstallCommand)));
        toolbar.Children.Add(install);
        toolbar.Children.Add(refresh);
        toolbar.Children.Add(openFolder);
        toolbar.Children.Add(toggle);
        toolbar.Children.Add(uninstall);

        var pathLabel = new TextBlock
        {
            Opacity = 0.6,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 8),
        };
        pathLabel.Bind(TextBlock.TextProperty, new Binding(nameof(InstalledPaksViewModel.ModsDir))
        { StringFormat = "Mods-Ordner: {0}" });

        var list = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x3C)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
        };
        list.Bind(ListBox.ItemsSourceProperty, new Binding(nameof(InstalledPaksViewModel.Mods)));
        list.Bind(ListBox.SelectedItemProperty, new Binding(nameof(InstalledPaksViewModel.Selected))
        { Mode = BindingMode.TwoWay });
        list.ItemTemplate = new FuncDataTemplate<PakRow>((row, _) =>
        {
            if (row is null) return null;
            var name = new TextBlock
            {
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(row.IsEnabled ? Color.FromRgb(0xE4, 0xE7, 0xEC) : Color.FromRgb(0x8A, 0x93, 0xA0)),
            };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(PakRow.FileName)));
            var meta = new TextBlock { Opacity = 0.75, FontSize = 11 };
            meta.Bind(TextBlock.TextProperty, new MultiBinding
            {
                Bindings =
                {
                    new Binding(nameof(PakRow.Size)),
                    new Binding(nameof(PakRow.StateLabel)),
                },
                Converter = new JoinConverter(),
            });
            return new StackPanel { Spacing = 2, Margin = new Thickness(6), Children = { name, meta } };
        }, supportsRecycling: true);

        var summary = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Opacity = 0.85 };
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(InstalledPaksViewModel.Summary)));

        Content = new DockPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                Make(toolbar,   DockPanel.DockProperty, Dock.Top),
                Make(pathLabel, DockPanel.DockProperty, Dock.Top),
                Make(summary,   DockPanel.DockProperty, Dock.Bottom),
                list,
            },
        };
    }

    private static Control Make(Control c, AvaloniaProperty property, object value)
    {
        c.SetValue(property, value);
        return c;
    }

    private sealed class JoinConverter : Avalonia.Data.Converters.IMultiValueConverter
    {
        public object? Convert(System.Collections.Generic.IList<object?> values,
            System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var v in values)
                if (v is string s && !string.IsNullOrWhiteSpace(s)) parts.Add(s);
            return string.Join("  ·  ", parts);
        }
    }
}
