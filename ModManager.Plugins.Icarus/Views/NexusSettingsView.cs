using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace ModManager.Plugins.Icarus.Views;

/// <summary>Nexus-Einstellungen-Tab: API-Key eintragen/löschen/prüfen +
/// Game-Slug + Cache-Refresh-Intervall. Alles in Kroste-Card-Look mit
/// Section-Cards.</summary>
public sealed class NexusSettingsView : UserControl
{
    public NexusSettingsView()
    {
        var apiKeySection = BuildApiKeySection();
        var generalSection = BuildGeneralSection();

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Thickness(20, 16, 20, 14),
                Children = { apiKeySection, generalSection },
            },
        };
        Content = scroll;
    }

    private static Control BuildApiKeySection()
    {
        var title = new TextBlock { Text = "API-Key" };
        title.Classes.Add("h2");

        var intro = new TextBlock
        {
            Text = "Persönlicher API-Key von nexusmods.com (kostenlos nach Registrierung). "
                + "Der Key wird lokal verschlüsselt gespeichert und ist nach der Eingabe nicht mehr sichtbar.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 12),
        };
        intro.Classes.Add("secondary");

        var openAccountBtn = new Button { Content = "🔗  Nexus-Account öffnen" };
        openAccountBtn.Classes.Add("ghost");
        openAccountBtn.Bind(Button.CommandProperty, new Binding(nameof(NexusSettingsViewModel.OpenNexusAccountCommand)));

        var status = new TextBlock { Margin = new Thickness(0, 0, 0, 8) };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(NexusSettingsViewModel.HasKey))
        {
            Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, string>(v =>
                v ? "✔ Key ist gespeichert (verschlüsselt)." : "✘ Kein Key konfiguriert."),
        });
        status.Classes.Add("muted");

        var input = new TextBox
        {
            [!TextBox.PlaceholderTextProperty] = new Binding { Source = "Neuen API-Key hier einfügen …" },
            PasswordChar = '•',
            Margin = new Thickness(0, 0, 0, 8),
        };
        input.Bind(TextBox.TextProperty, new Binding(nameof(NexusSettingsViewModel.ApiKeyInput))
        { Mode = BindingMode.TwoWay });

        var saveBtn = new Button { Content = "💾  Key speichern" };
        saveBtn.Classes.Add("accent");
        saveBtn.Bind(Button.CommandProperty, new Binding(nameof(NexusSettingsViewModel.SaveApiKeyCommand)));

        var clearBtn = new Button { Content = "🗑  Key löschen" };
        clearBtn.Classes.Add("danger");
        clearBtn.Bind(Button.CommandProperty, new Binding(nameof(NexusSettingsViewModel.ClearApiKeyCommand)));

        var verifyBtn = new Button { Content = "🔍  Verify" };
        verifyBtn.Bind(Button.CommandProperty, new Binding(nameof(NexusSettingsViewModel.VerifyCommand)));

        var buttonsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { saveBtn, clearBtn, verifyBtn, openAccountBtn },
        };

        var verifyResult = new TextBlock { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        verifyResult.Bind(TextBlock.TextProperty, new Binding(nameof(NexusSettingsViewModel.VerifyResult)));
        verifyResult.Bind(TextBlock.IsVisibleProperty, new Binding(nameof(NexusSettingsViewModel.HasVerifyResult)));

        var stack = new StackPanel
        {
            Children = { title, intro, status, input, buttonsRow, verifyResult },
        };
        var card = new Border { Child = stack };
        card.Classes.Add("card");
        return card;
    }

    private static Control BuildGeneralSection()
    {
        var title = new TextBlock { Text = "Allgemein" };
        title.Classes.Add("h2");

        var slugLabel = new TextBlock { Text = "Nexus-Game-Slug", Margin = new Thickness(0, 12, 0, 4) };
        slugLabel.Classes.Add("section-label");
        var slugBox = new TextBox();
        slugBox.Bind(TextBox.TextProperty, new Binding(nameof(NexusSettingsViewModel.GameSlug))
        { Mode = BindingMode.TwoWay });
        var slugHint = new TextBlock
        {
            Text = "Default: icarus. Nur ändern wenn Nexus den URL-Slug umbenennt.",
            Margin = new Thickness(0, 4, 0, 0), FontSize = 11,
        };
        slugHint.Classes.Add("muted");

        var refreshLabel = new TextBlock { Text = "Katalog-Cache Alter (Stunden)", Margin = new Thickness(0, 14, 0, 4) };
        refreshLabel.Classes.Add("section-label");
        var refreshBox = new NumericUpDown { Minimum = 1, Maximum = 168, Width = 120 };
        refreshBox.Bind(NumericUpDown.ValueProperty, new Binding(nameof(NexusSettingsViewModel.RefreshHours))
        { Mode = BindingMode.TwoWay });

        var saveBtn = new Button { Content = "💾  Speichern", Margin = new Thickness(0, 14, 0, 0) };
        saveBtn.Classes.Add("accent");
        saveBtn.Bind(Button.CommandProperty, new Binding(nameof(NexusSettingsViewModel.SaveGeneralCommand)));

        var stack = new StackPanel
        {
            Children = { title, slugLabel, slugBox, slugHint, refreshLabel, refreshBox, saveBtn },
        };
        var card = new Border { Child = stack };
        card.Classes.Add("card");
        return card;
    }
}
