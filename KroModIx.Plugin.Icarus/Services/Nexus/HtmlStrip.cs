using System.Net;
using System.Text.RegularExpressions;

namespace KroModIx.Plugin.Icarus.Services.Nexus;

/// <summary>Konvertiert Nexus-Mod-Beschreibungen (Mix aus HTML- und BBCode-
/// Formatierung) in Plain-Text. Nexus erlaubt beides parallel: alte User
/// tippen BBCode (<c>[font]</c>, <c>[color]</c>, <c>[list][*]…[/list]</c>),
/// neuere Uploads sind HTML — und einige mischen beide.
///
/// <para>Verhalten:</para>
/// <list type="bullet">
/// <item>BBCode-Listen-Items <c>[*]</c> werden zu „• "-Präfixen</item>
/// <item><c>[br]</c>, <c>&lt;br&gt;</c> und <c>&lt;/p&gt;</c> → Zeilenumbruch</item>
/// <item><c>&lt;li&gt;</c> → „• "</item>
/// <item><c>[url=…]Text[/url]</c> → nur „Text"; <c>[img]…[/img]</c> weg</item>
/// <item>Alle sonstigen BBCode- und HTML-Tags werden entfernt, Content bleibt</item>
/// <item>HTML-Entities (&amp;amp;, &amp;nbsp;, …) werden dekodiert</item>
/// <item>Mehrere Leerzeilen werden auf max. 2 reduziert</item>
/// </list>
/// </summary>
public static class HtmlStrip
{
    // BBCode (verarbeitet BEVOR die HTML-Regeln laufen — [br] soll nicht
    // versehentlich als HTML-Tag mit ecken verwechselt werden).
    private static readonly Regex BbBrRegex = new(@"\[br\s*/?\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BbHrRegex = new(@"\[hr\s*/?\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BbListItemRegex = new(@"\[\*\]", RegexOptions.Compiled);
    private static readonly Regex BbUrlNamedRegex = new(@"\[url=[^\]]*\](.*?)\[/url\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex BbImgRegex = new(@"\[img[^\]]*\].*?\[/img\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    // Generischer Fallback: alle restlichen [tag]/[/tag]/[tag=value] Wrapper
    // entfernen, Inhalt behalten. Passt zu [font], [size], [color], [b], [i],
    // [u], [list], [quote], [center], [code], [spoiler]…
    private static readonly Regex BbTagRegex = new(@"\[/?[a-zA-Z\*][a-zA-Z0-9]*(?:=[^\]]*)?\]", RegexOptions.Compiled);

    // HTML
    private static readonly Regex BrRegex = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PEndRegex = new(@"</p>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LiStartRegex = new(@"<li[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex MultiNewlineRegex = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex TrailingSpacesRegex = new(@"[ \t]+\n", RegexOptions.Compiled);

    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = html;

        // BBCode zuerst
        s = BbImgRegex.Replace(s, "");
        s = BbUrlNamedRegex.Replace(s, "$1");
        s = BbBrRegex.Replace(s, "\n");
        s = BbHrRegex.Replace(s, "\n");
        s = BbListItemRegex.Replace(s, "\n• ");
        s = BbTagRegex.Replace(s, "");

        // Dann HTML
        s = BrRegex.Replace(s, "\n");
        s = PEndRegex.Replace(s, "\n\n");
        s = LiStartRegex.Replace(s, "\n• ");
        s = TagRegex.Replace(s, "");

        s = WebUtility.HtmlDecode(s);
        s = TrailingSpacesRegex.Replace(s, "\n");
        s = MultiNewlineRegex.Replace(s, "\n\n");
        return s.Trim();
    }
}
