namespace Relay.Core.Tests;

/// <summary>A cURL copied on Windows has CRLF line endings; it must parse the same as on a Mac.</summary>
public class WindowsPasteTests
{
    const string Curl = """
        curl \
          --request POST \
          --url 'https://devgoapi.livekeeping.com/v1/pdf/getMyVoucherPDF' \
          --header 'token: abc.def.ghi' \
          --header 'Content-Type: application/json' \
          --data '{
          "requestFrom": "MOBILE",
          "voucherId": "6ad06637be7bb4c652918627",
          "isEway": true
        }'
        """;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void MultilineCurlParses(string newline)
    {
        var text = Curl.ReplaceLineEndings(newline);
        foreach (var request in new[] { CurlParser.Parse(text), CurlParser.ParsePasted(text)! })
        {
            Assert.Equal("POST", request.Method);
            Assert.Equal("https://devgoapi.livekeeping.com/v1/pdf/getMyVoucherPDF", request.Url);
            Assert.Contains(request.Headers, h => h.Key == "token" && h.Value == "abc.def.ghi");
            Assert.Equal(BodyMode.Json, request.Body.Mode);
            Assert.Contains("\"isEway\": true", request.Body.Raw);
            Assert.DoesNotContain("\r", request.Body.Raw);
        }
    }
}
