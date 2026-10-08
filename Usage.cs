using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UsageWidget;

public record UsageWindow(double Remaining, long? Reset, int? Minutes)
{
    public bool Expired => Reset is long r && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= r;
    public string Countdown => Reset is not long r ? "Reset time unavailable" : FormatCountdown(r, DateTimeOffset.UtcNow);
    public static string FormatCountdown(long reset, DateTimeOffset now)
    {
        var left = DateTimeOffset.FromUnixTimeSeconds(reset) - now;
        if (left <= TimeSpan.Zero) return "Awaiting updated quota";
        return left.TotalDays >= 1 ? $"Resets in {(int)left.TotalDays}d {left.Hours}h" : $"Resets in {(int)left.TotalHours}h {left.Minutes}m";
    }
    public static string State(double remaining) => remaining <= 10 ? "Low" : remaining <= 25 ? "Watch" : "Healthy";
}
public record UsageSnapshot(string Plan, string Account, UsageWindow? FiveHour, UsageWindow? Weekly, bool Blocked)
{
    public static UsageSnapshot Parse(JsonElement root)
    {
        JsonElement bucket;
        if (root.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            if (!map.TryGetProperty("codex", out bucket)) throw new InvalidDataException("Work/Codex quota unavailable.");
        }
        else if (!root.TryGetProperty("rateLimits", out bucket)) throw new InvalidDataException("Usage response changed.");
        UsageWindow? Read(string key)
        {
            if (!bucket.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object ||
                !w.TryGetProperty("usedPercent", out var percent) || !percent.TryGetDouble(out var used) || !double.IsFinite(used)) return null;
            long? reset = w.TryGetProperty("resetsAt", out var rr) && rr.TryGetInt64(out var r) && r >= 0 && r <= 253402300799 ? r : null;
            int? mins = w.TryGetProperty("windowDurationMins", out var mm) && mm.TryGetInt32(out var m) ? m : null;
            return new(Math.Clamp(100 - used, 0, 100), reset, mins);
        }
        var windows = new[] { Read("primary"), Read("secondary") };
        string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var blocked = root.TryGetProperty("ordinaryUsageAllowed", out var allowed) && allowed.ValueKind == JsonValueKind.False;
        blocked |= bucket.TryGetProperty("spendControlReached", out var spend) && spend.ValueKind == JsonValueKind.True;
        blocked |= !string.IsNullOrEmpty(Str(bucket, "rateLimitReachedType"));
        return new(Str(bucket, "planType"), Str(root, "accountId"), windows.FirstOrDefault(w => w?.Minutes == 300), windows.FirstOrDefault(w => w?.Minutes == 10080), blocked);
    }
}

public sealed class UsageClient
{
    public static string FindCodex(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : throw new FileNotFoundException("Selected Codex executable is missing.");
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var file = Path.Combine(dir, "codex.exe");
            if (File.Exists(file)) return file;
        }
        var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(bundled))
        {
            var file = Directory.GetFiles(bundled, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (file != null) return file;
        }
        throw new FileNotFoundException("Install Codex or choose codex.exe in Settings.");
    }
    public async Task<UsageSnapshot> ReadAsync(string path, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        using var process = new Process { StartInfo = new ProcessStartInfo(FindCodex(path)) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("app-server");
        process.StartInfo.ArgumentList.Add("--listen");
        process.StartInfo.ArgumentList.Add("stdio://");
        try
        {
            process.Start();
            // Drain diagnostics without storing or exposing tokens or account details.
            var drain = Task.Run(async () => { while (await process.StandardError.ReadLineAsync(token) != null) { } }, token);
            async Task<JsonElement> Call(int id, string method, object? parameters)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }).AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(token);
                    if (line == null) throw new IOException("Codex exited before returning usage.");
                    using var doc = JsonDocument.Parse(line);
                    var msg = doc.RootElement;
                    if (!msg.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var number) || number != id) continue;
                    if (msg.TryGetProperty("error", out _)) throw new InvalidOperationException("Codex could not read usage. Check ChatGPT sign-in or connection.");
                    return msg.GetProperty("result").Clone();
                }
            }
            await Call(1, "initialize", new { clientInfo = new { name = "custom_usage_widget", title = "Usage Widget", version = "1.0.0" } });
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
            var result = await Call(2, "account/rateLimits/read", null);
            return UsageSnapshot.Parse(result);
        }
        finally
        {
            timeout.Cancel();
            try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } } catch (InvalidOperationException) { }
        }
    }
}

public sealed class Settings
{
    public string Theme { get; set; } = "System";
    public bool Notifications { get; set; }
    public int RefreshSeconds { get; set; } = 120;
    public int RotateSeconds { get; set; } = 8;
    public bool DockToTaskbar { get; set; } = true;
    public int LayoutVersion { get; set; }
    public string CodexPath { get; set; } = "";
    public double? Left { get; set; }
    public double? Top { get; set; }
    public List<string> SentAlerts { get; set; } = new();
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CustomUsageWidget", "settings.json");
    public static Settings Load()
    {
        try {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); s.SentAlerts ??= new();
            if (s.Left is double x && (!double.IsFinite(x) || Math.Abs(x) > 100000)) s.Left = null;
            if (s.Top is double y && (!double.IsFinite(y) || Math.Abs(y) > 100000)) s.Top = null;
            if (s.Theme != "System" && s.Theme != "Dark" && s.Theme != "Light") s.Theme = "System";
            s.RefreshSeconds = Math.Clamp(s.RefreshSeconds, 60, 600); s.CodexPath ??= "";
            s.RotateSeconds = Math.Clamp(s.RotateSeconds, 3, 60);
            if (s.LayoutVersion < 2) { s.Left = null; s.Top = null; s.LayoutVersion = 2; }
            return s;
        }
        catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}

// A closed app rearms manual hiding for its next launch. Background renderer
// processes alone do not count as an open desktop window.
public sealed class AppPresenceState
{
    public bool Running { get; private set; }
    public bool ManuallyHidden { get; private set; }
    public bool ShouldShow => Running && !ManuallyHidden;
    public bool Update(bool running) { var changed = Running != running; Running = running; if (!running) ManuallyHidden = false; return changed; }
    public void Hide() => ManuallyHidden = true;
    public void Restore() => ManuallyHidden = false;
}

public static class Alerts
{
    public static IEnumerable<int> Evaluate(Settings settings, UsageSnapshot snapshot, UsageWindow? window, string name)
    {
        if (!settings.Notifications || window == null || window.Expired || window.Reset == null || string.IsNullOrEmpty(snapshot.Account)) yield break;
        foreach (var threshold in new[] { 25, 10, 5 })
        {
            var key = $"{snapshot.Account}:{name}:{window.Reset}:{threshold}";
            if (window.Remaining <= threshold && !settings.SentAlerts.Contains(key)) { settings.SentAlerts.Add(key); yield return threshold; }
        }
    }
}
