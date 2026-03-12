using System.Text.Json;

namespace Muxer.Android.Services;

public static class ServerSettings
{
    public const string DefaultServer = "http://192.168.0.65:5199";
    private const string Key = "extra_servers";

    public static List<string> GetExtraServers()
    {
        var json = Preferences.Get(Key, "[]");
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    public static void SaveExtraServers(List<string> servers)
    {
        Preferences.Set(Key, JsonSerializer.Serialize(servers));
    }

    public static List<string> GetAllServers()
    {
        var all = new List<string> { DefaultServer };
        all.AddRange(GetExtraServers());
        return all;
    }
}
