using System.Diagnostics;

namespace Relay.Core.Tests;

public class JsonFormatTests
{
    [Fact]
    public void KeepsKeyOrder()
    {
        var pretty = JsonFormat.Pretty("""{"z":1,"a":2,"m":{"y":true,"b":null}}""");
        Assert.Equal("""
        {
          "z": 1,
          "a": 2,
          "m": {
            "y": true,
            "b": null
          }
        }
        """.ReplaceLineEndings("\n"), pretty);
    }

    [Fact]
    public void LeavesNumbersAsWritten() =>
        Assert.Equal("[\n  1.0,\n  1e5,\n  -0.5,\n  2E-3\n]", JsonFormat.Pretty("[1.0,1e5,-0.5,2E-3]"));

    [Fact]
    public void StringsWithEscapesAndStructuralCharacters()
    {
        var source = """{"a":"say \"hi\", {ok} [x]: \\","b":"\u00e9"}""";
        Assert.Equal("""
        {
          "a": "say \"hi\", {ok} [x]: \\",
          "b": "\u00e9"
        }
        """.ReplaceLineEndings("\n"), JsonFormat.Pretty(source));
    }

    [Fact]
    public void EmptyContainersStayOnOneLine()
    {
        Assert.Equal("{\n  \"a\": {},\n  \"b\": []\n}", JsonFormat.Pretty("""{"a":{ },"b":[ ]}"""));
        Assert.Equal("[]", JsonFormat.Pretty("[]"));
    }

    [Fact]
    public void NestedArrays() =>
        Assert.Equal("[\n  [\n    1,\n    2\n  ],\n  [\n    3\n  ]\n]", JsonFormat.Pretty("[[1,2],[3]]"));

    [Fact]
    public void UnicodePassesThrough() =>
        Assert.Equal("{\n  \"name\": \"Zoë 🚀 日本\"\n}", JsonFormat.Pretty("""{"name":"Zoë 🚀 日本"}"""));

    [Fact]
    public void CustomIndent() =>
        Assert.Equal("{\n    \"a\": 1\n}", JsonFormat.Pretty("""{"a":1}""", indent: 4));

    [Fact]
    public void NotJson()
    {
        Assert.Null(JsonFormat.Pretty("<html></html>"));
        Assert.Null(JsonFormat.Pretty("""{"a":}"""));
        Assert.Null(JsonFormat.Pretty(""));
        Assert.False(JsonFormat.IsJson("hello"));
        Assert.True(JsonFormat.IsJson("  42 "));
    }

    [Fact]
    public void Minify() =>
        Assert.Equal("""{"a":[1,2],"b":"x y"}""", JsonFormat.Minify("{\n  \"a\" : [ 1, 2 ],\n  \"b\": \"x y\"\n}"));

    [Fact]
    public void HighlightTokens()
    {
        var text = """{"id": 7, "ok": true, "name": "x"}""";
        var tokens = JsonFormat.HighlightRanges(text).Select(t => (Text: text.Substring(t.Start, t.Length), t.Token)).ToList();
        var nonPunctuation = tokens.Where(t => t.Token != JsonFormat.Token.Punctuation).ToList();
        Assert.Equal(["\"id\"", "7", "\"ok\"", "true", "\"name\"", "\"x\""], nonPunctuation.Select(t => t.Text));
        Assert.Equal(
            [JsonFormat.Token.Key, JsonFormat.Token.Number, JsonFormat.Token.Key, JsonFormat.Token.Literal, JsonFormat.Token.Key, JsonFormat.Token.String],
            nonPunctuation.Select(t => t.Token));
        Assert.Equal(JsonFormat.Token.Punctuation, tokens[0].Token);
    }

    [Fact]
    public void HighlightRangesAreUtf16()
    {
        var text = """{"🚀": "é"}""";
        var key = JsonFormat.HighlightRanges(text).First(t => t.Token == JsonFormat.Token.Key);
        Assert.Equal("\"🚀\"", text.Substring(key.Start, key.Length));
    }

    [Fact]
    public void XmlPretty()
    {
        Assert.Equal("<a x=\"1\">\n  <b>hi</b>\n  <c/>\n</a>", XmlFormat.Pretty("""<a x="1"><b>hi</b><c/></a>"""));
        Assert.Null(XmlFormat.Pretty("{}"));
    }

    [Fact]
    public void LargeDocumentIsFast()
    {
        var item = """{"id":123456,"name":"Item name here","tags":["a","b","c"],"price":12.5,"active":true},""";
        var body = "[" + string.Concat(Enumerable.Repeat(item, 5_000_000 / item.Length)) + """{"end":null}]""";
        var watch = Stopwatch.StartNew();
        Assert.NotNull(JsonFormat.Pretty(body));
        // Generous: tests may run unoptimised.
        Assert.True(watch.Elapsed.TotalSeconds < 10);
    }
}
