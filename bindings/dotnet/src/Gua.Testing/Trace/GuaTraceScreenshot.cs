using System.Text.Json;

namespace Gua.Testing;

public static partial class GuaTraceCapture
{
    /// <summary>Retains an already captured/masked PNG only when the caller explicitly authorizes pixels
    /// for this Trace's profile. Does not acquire screenshots, elevate host access, or detect image secrets.
    /// observationId identifies the UI observation whose physical-pixel bounds may be overlaid.</summary>
    public static bool Screenshot(GuaTraceSession trace, string stepId, JsonElement screenshot,
        string observationId, bool pixelsAuthorized)
    {
        try
        {
            if (!pixelsAuthorized || string.IsNullOrEmpty(observationId)) throw new JsonException();
            var width = screenshot.GetProperty("width").GetInt32();
            var height = screenshot.GetProperty("height").GetInt32();
            var uri = screenshot.GetProperty("dataUri").GetString()!;
            const string prefix = "data:image/png;base64,";
            if (width < 1 || height < 1 || width > 8192 || height > 8192 || (long)width * height > 16777216 ||
                uri.Length > 4 * 1024 * 1024 || !uri.StartsWith(prefix, StringComparison.Ordinal)) throw new JsonException();
            var encoded = uri.Substring(prefix.Length);
            if (encoded.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=')))
                throw new JsonException();
            var png = Convert.FromBase64String(encoded);
            var signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82 };
            if (png.Length < 33 || !signature.SequenceEqual(png.Take(16))) throw new JsonException();
            long Dimension(int offset) => ((long)png[offset] << 24) | ((long)png[offset + 1] << 16) | ((long)png[offset + 2] << 8) | png[offset + 3];
            if (Dimension(16) != width || Dimension(20) != height) throw new JsonException();
            var content = GuaTraceJson.Element(new { schemaVersion = 1, profile = trace.ObservationProfile,
                pixelPolicy = "caller-authorized", observationId, screenshot = new { dataUri = uri, width, height } });
            if (!trace.ObservationRedactionIsUnchanged(content)) throw new JsonException();
            return trace.Attach(stepId, "gua.trace.screenshot.v1", content);
        }
        catch
        {
            trace.ObservationIssue("screenshot-unavailable");
            trace.Record(stepId, "capture.failure", GuaTraceJson.Element(new { channel = "screenshot", reason = "unavailable-or-not-authorized" }));
            return false;
        }
    }
}
