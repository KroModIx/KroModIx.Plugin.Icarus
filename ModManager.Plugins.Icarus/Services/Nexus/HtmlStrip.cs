using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ModManager.Plugins.Icarus.Services.Nexus;

/// <summary>Simpler HTML-zu-Plain-Text-Konverter für Nexus-Mod-Beschreibungen.
/// Kein HtmlAgilityPack (Extra-Dep vermeiden); ein Regex reicht — Nexus-
/// Descriptions haben nur ein handvoll Tags (&lt;p&gt;, &lt;br /&gt;, &lt;a&gt;,
/// &lt;h1&gt;-&lt;h6&gt;, &lt;b&gt;, &lt;i&gt;, &lt;ul&gt;, &lt;li&gt;, &lt;font&gt;).
///
/// <para>Verhalten:</para>
/// <list type="bullet">
/// <item>&lt;br&gt; und &lt;/p&gt; werden zu Zeilenumbrüchen</item>
/// <item>&lt;li&gt; wird zu „• "</item>
/// <item>Alle anderen Tags werden entfernt</item>
/// <item>HTML-Entities (&amp;amp;, &amp;lt;, &amp;nbsp;, …) werden dekodiert</item>
/// <item>Mehrere Leerzeilen werden auf max. 2 reduziert</item>
/// </list>
/// </summary>
public static class HtmlStrip
{
    private static readonly Regex BrRegex = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PEndRegex = new(@"</p>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LiStartRegex = new(@"<li[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex MultiNewlineRegex = new(@"\n{3,}", RegexOptions.Compiled);

    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var sb = new StringBuilder(html.Length);
        var s = BrRegex.Replace(html, "\n");
        s = PEndRegex.Replace(s, "\n\n");
        s = LiStartRegex.Replace(s, "\n• ");
        s = TagRegex.Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = MultiNewlineRegex.Replace(s, "\n\n");
        return s.Trim();
    }
}
