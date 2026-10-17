using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Principal;
using PollLagTester;

var opt = AppOptions.Parse(args);
if (opt.ShowHelp)
{
    Console.WriteLine(AppOptions.Usage);
    return;
}

string url = $"http://localhost:{opt.Port}/";

Mutex? mutex = null;
bool owned = false;
try
{
    mutex = new Mutex(true, @"Local\PollLagTester", out owned);
    if (!owned && opt.Restarted)
    {
        // Elevated restart: wait for the previous instance to exit and release the port.
        try { owned = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
        catch (AbandonedMutexException) { owned = true; }
    }
}
catch (UnauthorizedAccessException) { }
if (!owned)
{
    Console.WriteLine("Poll//Lag Tester is already running: " + url);
    if (!opt.NoBrowser) OpenBrowser(url);
    return;
}

Native.OptOutOfPowerThrottling();

string baseDir = AppContext.BaseDirectory;
var collector = new RawInputCollector();
collector.Start(opt.Mode);
using var latency = new LatencyTracker(baseDir);
var store = new SessionStore(Path.Combine(baseDir, "sessions"));
var analyzer = new Analyzer(collector, latency, store, opt.Mode);
analyzer.Start();

var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k =>
{
    if (opt.Lan) k.ListenAnyIP(opt.Port);
    else k.ListenLocalhost(opt.Port);
});
var app = builder.Build();
var files = StaticFiles.Load();

byte[] sseData = "data: "u8.ToArray(), sseEnd = "\n\n"u8.ToArray();

async Task Stream(HttpContext ctx, Func<byte[]> source, double defaultHz, double maxHz)
{
    double hz = double.TryParse(ctx.Request.Query["hz"], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
        ? Math.Clamp(h, 0.5, maxHz) : defaultHz;
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    var ct = ctx.RequestAborted;
    var period = TimeSpan.FromSeconds(1 / hz);
    try
    {
        while (!ct.IsCancellationRequested)
        {
            await ctx.Response.Body.WriteAsync(sseData, ct);
            await ctx.Response.Body.WriteAsync(source(), ct);
            await ctx.Response.Body.WriteAsync(sseEnd, ct);
            await ctx.Response.Body.FlushAsync(ct);
            await Task.Delay(period, ct);
        }
    }
    catch (OperationCanceledException) { }
    catch (IOException) { }
}

app.MapGet("/api/snapshot", () => Results.Bytes(analyzer.Json, "application/json"));
app.MapGet("/api/stream", (HttpContext ctx) => Stream(ctx, () => analyzer.Json, 2, 4));
app.MapGet("/api/gamepad/stream", (HttpContext ctx) => Stream(ctx, () => analyzer.PadJson, 30, 60));

app.MapPost("/api/mode", async (string m) =>
    AppOptions.TryParseMode(m, out var mode) ? Results.Ok(await analyzer.SetModeAsync(mode)) : Results.BadRequest("unknown mode"));
app.MapPost("/api/device", async (string id) =>
{
    if (id != "auto" && !long.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) return Results.BadRequest("bad id");
    return Results.Ok(await analyzer.SelectAsync(id));
});
app.MapPost("/api/live/reset", async () => Results.Ok(await analyzer.ResetLiveAsync()));
app.MapPost("/api/session/start", async () => Results.Ok(await analyzer.StartSessionAsync()));
app.MapPost("/api/session/stop", async () => Results.Json(new { id = await analyzer.StopSessionAsync() }));
app.MapPost("/api/gamepad/drift", async (double? seconds) => Results.Ok(await analyzer.StartDriftAsync(seconds ?? 5)));
app.MapPost("/api/gamepad/circularity/reset", async () => Results.Ok(await analyzer.ResetCircularityAsync()));

app.MapGet("/api/sessions", () => Results.Json(store.List()));
app.MapGet("/api/sessions/{id}", (string id, bool? download) =>
{
    var b = store.Get(id);
    if (b == null) return Results.NotFound();
    return download == true ? Results.File(b, "application/json", $"polllag-{id}.json") : Results.Bytes(b, "application/json");
});
app.MapDelete("/api/sessions/{id}", (string id) => store.Delete(id) ? Results.Ok() : Results.NotFound());

app.MapPost("/api/latency/start", (string? target) =>
    latency.Start(target, out var err) ? Results.Ok() : Results.BadRequest(err));
app.MapPost("/api/latency/stop", () => { latency.Stop(); return Results.Ok(); });
app.MapPost("/api/latency/download", async () =>
{
    try { return Results.Json(new { path = await latency.DownloadAsync() }); }
    catch (Exception ex) { return Results.BadRequest(ex.Message); }
});
app.MapPost("/api/restart-admin", (IHostApplicationLifetime life) =>
{
    if (Elevation.IsAdmin) return Results.BadRequest("Already running as administrator.");
    if (!Elevation.RestartElevated(opt)) return Results.BadRequest("Elevation was cancelled.");
    _ = Task.Run(async () => { await Task.Delay(300); life.StopApplication(); });
    return Results.Ok();
});

// The default fallback pattern skips paths that look like files, so match everything explicitly.
app.MapFallback("{**path}", (HttpContext ctx) => files.Serve(ctx));

try
{
    await app.StartAsync();
}
catch (IOException ex)
{
    Console.Error.WriteLine($"Could not listen on port {opt.Port}: {ex.Message}. Try --port <n>.");
    return;
}

Console.WriteLine($"""

   POLL//LAG TESTER  ·  polling rate & input lag monitor
   ----------------------------------------------------
   Dashboard : {url}{(opt.Lan ? $"  (also on your LAN at port {opt.Port})" : "")}
   Mode      : {Analyzer.ModeName(opt.Mode)}
   Hotkey    : {(collector.HotkeyRegistered ? "Ctrl+Shift+F9 starts/stops a session in-game" : "unavailable (Ctrl+Shift+F9 is taken)")}
   Admin     : {(Elevation.IsAdmin ? "yes (input lag via PresentMon available)" : "no (polling works; input lag needs admin)")}
   Ctrl+C to quit.

""");
if (!opt.NoBrowser) OpenBrowser(url);

await app.WaitForShutdownAsync();
latency.Stop();
analyzer.Stop();
collector.Stop();
GC.KeepAlive(mutex);

static void OpenBrowser(string url)
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    catch (Win32Exception) { }
}

internal sealed class AppOptions
{
    public int Port = 7272;
    public bool Lan, NoBrowser, Restarted, ShowHelp;
    public InputMode Mode = InputMode.Mouse;

    public const string Usage = """
        PollLagTester [options]
          --mode <mouse|keyboard|controller>   input source to start with (default mouse)
          --port <n>                           dashboard port (default 7272)
          --lan                                also serve the dashboard on your local network (view it on a phone)
          --no-browser                         don't open the dashboard automatically
        """;

    public static AppOptions Parse(string[] args)
    {
        var o = new AppOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): o.Port = p; i++; break;
                case "--mode" when i + 1 < args.Length && TryParseMode(args[i + 1], out var m): o.Mode = m; i++; break;
                case "--lan": o.Lan = true; break;
                case "--no-browser": o.NoBrowser = true; break;
                case "--restarted": o.Restarted = true; break;
                case "-h" or "--help" or "/?": o.ShowHelp = true; break;
            }
        }
        return o;
    }

    public static bool TryParseMode(string? s, out InputMode mode)
    {
        switch (s?.ToLowerInvariant())
        {
            case "mouse": mode = InputMode.Mouse; return true;
            case "keyboard": mode = InputMode.Keyboard; return true;
            case "controller" or "gamepad": mode = InputMode.Controller; return true;
            default: mode = InputMode.Mouse; return false;
        }
    }

    public string RestartArgs() =>
        $"--port {Port} --mode {Analyzer.ModeName(Mode)} --no-browser --restarted{(Lan ? " --lan" : "")}";
}

internal static class Elevation
{
    public static readonly bool IsAdmin = Check();

    static bool Check()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool RestartElevated(AppOptions o)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, o.RestartArgs()) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception) { return false; }
    }
}

internal sealed class StaticFiles
{
    readonly Dictionary<string, (byte[] Data, string Type)> _files = new(StringComparer.OrdinalIgnoreCase);

    public static StaticFiles Load()
    {
        var sf = new StaticFiles();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("wwwroot/", StringComparison.Ordinal)) continue;
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var rel = "/" + name["wwwroot/".Length..].Replace('\\', '/');
            sf._files[rel] = (ms.ToArray(), ContentType(rel));
        }
        return sf;
    }

    static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".woff" => "font/woff",
        ".png" => "image/png",
        _ => "application/octet-stream",
    };

    public IResult Serve(HttpContext ctx)
    {
        var p = ctx.Request.Path.Value ?? "/";
        if (p == "/") p = "/index.html";
        return _files.TryGetValue(p, out var f) ? Results.Bytes(f.Data, f.Type) : Results.NotFound();
    }
}
