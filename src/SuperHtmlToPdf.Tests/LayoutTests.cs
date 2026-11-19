using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SuperHtmlToPdf.Tests;

[TestClass]
public sealed class LayoutTests
{
    private static List<List<Drawing>> Render(string html, string css, double width, double height, List<string>? warnings = null)
    {
        void Warn(string code, string message) => warnings?.Add(code);
        var document = HtmlParser.Parse(html, 20_000, 100, CancellationToken.None);
        CssCascade.Apply(document, ["body { margin: 0 } " + css], Warn, CancellationToken.None);
        return new LayoutEngine(new RenderAssets([]), Warn, CancellationToken.None).Paginate(document, width, height, 100);
    }
    private static DrawText Find(IEnumerable<Drawing> page, string label) => page.OfType<DrawText>().Single(t => t.Value == label);

    [TestMethod]
    public void FlexUsesGrowthOrderAndWrapping()
    {
        var output = Render("<body><main><div>A</div><div>B</div><div>C</div></main></body>",
            "main{display:flex;flex-wrap:wrap;gap:10pt} main div{flex:1 1 85pt} main div:nth-child(2){order:-1}", 190, 120).Single();
        var a = Find(output, "A");
        var b = Find(output, "B");
        var c = Find(output, "C");
        Assert.AreEqual(0, b.X, 0.01);
        Assert.AreEqual(100, a.X, 0.01);
        Assert.AreEqual(a.Baseline, b.Baseline, 0.01);
        Assert.AreEqual(0, c.X, 0.01);
        Assert.IsTrue(c.Baseline > b.Baseline + 10);
    }

    [TestMethod]
    public void GridResolvesMinmaxRepeatSpansAndImplicitRow()
    {
        var output = Render("<body><main><div>A</div><div>B</div><div>C</div></main></body>",
            "main{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10pt} main div:first-child{grid-column:span 2}", 170, 120).Single();
        var a = Find(output, "A");
        var b = Find(output, "B");
        var c = Find(output, "C");
        Assert.AreEqual(120, b.X, 0.01);
        Assert.AreEqual(a.Baseline, b.Baseline, 0.01);
        Assert.AreEqual(0, c.X, 0.01);
        Assert.IsTrue(c.Baseline > a.Baseline + 10);
    }

    [TestMethod]
    public void RepeatedHeaderNeverDuplicatesABodyRow()
    {
        var labels = Enumerable.Range(0, 40).Select(i => $"Row{i:00}").ToArray();
        var html = "<table><thead><tr><th>Header</th></tr></thead><tbody>" +
            string.Concat(labels.Select(label => $"<tr><td>{label}</td></tr>")) + "</tbody></table>";
        var pages = Render(html, "th,td{padding:2pt}", 170, 72);
        Assert.IsTrue(pages.Count > 5);
        foreach (var page in pages)
        {
            var header = Find(page, "Header");
            Assert.IsTrue(page.OfType<DrawText>().Any(t => t.Value.StartsWith("Row") && t.Baseline > header.Baseline));
        }
        CollectionAssert.AreEqual(labels, pages.SelectMany(p => p.OfType<DrawText>()).Where(t => t.Value.StartsWith("Row")).Select(t => t.Value).ToArray());
    }

    [TestMethod]
    public void SingleParagraphWrapsAcrossPagesWithoutLosingOrRepeatingWords()
    {
        var words = Enumerable.Range(0, 150).Select(i => $"Mot{i:000}").ToArray();
        var pages = Render("<p>" + string.Join(' ', words) + "</p>", "p{margin:0;font-size:9pt;line-height:12pt;orphans:2;widows:2}", 100, 63);
        Assert.IsTrue(pages.Count >= 5);
        var text = pages.SelectMany(p => p.OfType<DrawText>()).Where(t => t.Value.StartsWith("Mot")).ToArray();
        CollectionAssert.AreEqual(words, text.Select(t => t.Value).ToArray());
        Assert.IsTrue(text.All(t => t.Baseline >= 0 && t.Baseline <= 63));
    }

    [TestMethod]
    public void ExplicitBreaksAndTallRowsAlwaysAdvance()
    {
        var tall = string.Join("<br>", Enumerable.Range(0, 16).Select(i => $"L{i:00}"));
        var pages = Render($"<div>Start</div><table style='break-before:page'><tr style='break-inside:avoid'><td>{tall}</td></tr></table><div style='break-before:page'>End</div>",
            "table{border-spacing:0}", 120, 72);
        Assert.AreEqual("Start", pages[0].OfType<DrawText>().Single().Value);
        Assert.AreEqual("End", pages[^1].OfType<DrawText>().Single().Value);
        var rows = pages.SelectMany(p => p.OfType<DrawText>()).Where(t => t.Value.StartsWith('L')).Select(t => t.Value).ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(0, 16).Select(i => $"L{i:00}").ToArray(), rows);
    }

    [TestMethod]
    public void FloatsClearAndAbsoluteBottomUsesDefiniteHeight()
    {
        var html = "<main><div class='left'>A</div><div class='right'>B</div><div class='below'>C</div></main><section><div class='absolute'>D</div></section>";
        var css = ".left{float:left;width:50pt;height:30pt}.right{float:right;width:40pt;height:30pt}.below{clear:both}" +
            "section{position:relative;height:50pt}.absolute{position:absolute;bottom:0;left:5pt}";
        var output = Render(html, css, 170, 160).Single();
        var a = Find(output, "A");
        var b = Find(output, "B");
        var c = Find(output, "C");
        var d = Find(output, "D");
        Assert.AreEqual(130, b.X, 0.01);
        Assert.AreEqual(a.Baseline + 30, c.Baseline, 0.01);
        Assert.AreEqual(c.Baseline + 50, d.Baseline, 0.01);
        Assert.AreEqual(5, d.X, 0.01);
    }

    [TestMethod]
    public void ReportsUnsupportedClippingAndNamedGridAreas()
    {
        var diagnostics = new List<string>();
        Render("<div style='overflow:hidden;border-radius:3pt;display:grid;grid-template-areas:&quot;name&quot;'><b>A</b></div>", "", 100, 72, diagnostics);
        CollectionAssert.Contains(diagnostics, "css-overflow-clip");
        CollectionAssert.Contains(diagnostics, "css-border-radius");
        CollectionAssert.Contains(diagnostics, "css-grid-areas");
    }

    [TestMethod]
    public void GridValidatesTrackListsAndAtomicBounds()
    {
        Assert.HasCount(2, LayoutValues.SplitTracks("12px minmax(0, 1fr)", 2));
        Assert.ThrowsExactly<InvalidDataException>(() => LayoutValues.SplitTracks("12px auto 1fr", 2));
        var diagnostics = new List<string>();
        var page = Render("<main style='display:grid;grid-template-columns:minmax(calc(10pt + 2pt),1fr)'><div>A</div></main>",
            "", 100, 72, diagnostics).Single();
        Assert.AreEqual(0, Find(page, "A").X, 0.01);
        CollectionAssert.Contains(diagnostics, "css-grid-minmax");
        Assert.AreEqual(12, LayoutValues.Length("calc(10pt + 2pt)", 100, 10), 0.01);
    }
}
