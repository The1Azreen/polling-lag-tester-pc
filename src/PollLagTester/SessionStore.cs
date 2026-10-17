using System.Text.Json;
using System.Text.RegularExpressions;

namespace PollLagTester;

/// <summary>Saved sessions: one JSON file each under ./sessions next to the exe.</summary>
internal sealed class SessionStore(string dir)
{
    static readonly Regex IdRx = new(@"^\d{8}-\d{6}$", RegexOptions.Compiled);

    string PathFor(string id) => Path.Combine(dir, id + ".json");

    public void Save(string id, object doc)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(PathFor(id), JsonSerializer.SerializeToUtf8Bytes(doc, Analyzer.JsonOpts));
    }

    public List<JsonElement> List()
    {
        var list = new List<JsonElement>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.GetFiles(dir, "*.json").OrderByDescending(f => f, StringComparer.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(f));
                var r = doc.RootElement;
                var o = new Dictionary<string, JsonElement>();
                foreach (var key in new[] { "id", "startedAt", "durationSec", "mode", "headline" })
                    if (r.TryGetProperty(key, out var v)) o[key] = v.Clone();
                list.Add(JsonSerializer.SerializeToElement(o));
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return list;
    }

    public byte[]? Get(string id) => IdRx.IsMatch(id) && File.Exists(PathFor(id)) ? File.ReadAllBytes(PathFor(id)) : null;

    public bool Delete(string id)
    {
        if (!IdRx.IsMatch(id) || !File.Exists(PathFor(id))) return false;
        File.Delete(PathFor(id));
        return true;
    }
}
