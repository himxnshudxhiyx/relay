namespace Relay.Core.Tests;

public class JsonFoldingTests
{
    static readonly string Json = """
    {
      "note": "not a block {",
      "items": [
        {
          "id": 1
        },
        {
          "id": 2
        }
      ],
      "inline": [1, 2]
    }
    """.ReplaceLineEndings("\n");

    [Fact]
    public void FoldsAndUnfoldsBlocks()
    {
        var f = new JsonFolding(Json);
        // Root, items, and the two elements; the string's "{" and the one-line array aren't blocks.
        Assert.Equal(4, f.AllRegions.Count);
        Assert.Equal(Json, f.Display);

        var items = f.Markers[Json.IndexOf("  \"items\"", StringComparison.Ordinal)];
        f.Toggle(items + 1);  // first element
        Assert.Contains("    {…},\n    {\n", f.Display);
        var after = f.Display.IndexOf("{…},\n", StringComparison.Ordinal) + 5;
        Assert.Equal(2, f.HiddenLines(after));  // line numbers skip the folded element's lines
        Assert.Equal(0, f.HiddenLines(0));
        var dots = f.Display.IndexOf('…');
        Assert.Equal(items + 1, f.Placeholder(dots - 1));  // clicking { … or } finds the fold
        Assert.Equal(items + 1, f.Placeholder(dots + 1));
        Assert.Null(f.Placeholder(dots + 2));  // the comma after it doesn't

        f.Toggle(items);      // the whole list, with an element folded inside
        Assert.Contains("\"items\": […],\n  \"inline\"", f.Display);
        Assert.Equal(2, f.Markers.Count);  // hidden elements lose their markers

        f.Toggle(items);
        f.Toggle(items + 1);
        Assert.Equal(Json, f.Display);  // unfolding everything restores the original text
    }
}
