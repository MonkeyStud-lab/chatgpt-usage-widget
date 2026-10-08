using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UsageWidget;
public static class SelfTests
{
    public static int Run(string[] args)
    {
        int count = 0;
        void Check(bool valid, string message) { if (!valid) throw new Exception(message); count++; }
        UsageSnapshot Parse(string json) { using var doc = JsonDocument.Parse(json); return UsageSnapshot.Parse(doc.RootElement); }
        try
        {
            var future = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds();
            string bucket = $"{{\"planType\":\"plus\",\"primary\":{{\"usedPercent\":29,\"windowDurationMins\":300,\"resetsAt\":{future}}},\"secondary\":{{\"usedPercent\":5,\"windowDurationMins\":10080,\"resetsAt\":{future}}}}}";
            var s = Parse("{\"accountId\":\"test\",\"rateLimits\":" + bucket + "}");
            Check(s.Plan == "plus" && s.FiveHour?.Remaining == 71 && s.Weekly?.Remaining == 95, "Remaining / plan parsing");
            var mapped = Parse("{\"rateLimits\":{},\"rateLimitsByLimitId\":{\"codex\":" + bucket + "}}");
            Check(mapped.FiveHour?.Remaining == 71, "Multi-bucket priority");
            Check(Parse("{\"rateLimits\":{}}").FiveHour == null, "Missing data cannot become zero or 100");
            Check(Parse("{\"ordinaryUsageAllowed\":false,\"rateLimits\":" + bucket + "}").Blocked, "Restriction must survive nonzero remaining quota");
            Check(Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":130,\"windowDurationMins\":300}}}").FiveHour?.Remaining == 0, "Clamp percent");
            Check(Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":10,\"windowDurationMins\":15}}}").FiveHour == null, "Do not mislabel unknown windows");
            Check(UsageWindow.State(26) == "Healthy" && UsageWindow.State(25) == "Watch" && UsageWindow.State(10) == "Low", "Threshold boundaries");
            Check(UsageWindow.FormatCountdown(100, DateTimeOffset.FromUnixTimeSeconds(100)) == "Awaiting updated quota", "Expiry must not invent reset");
            var settings = new Settings { Notifications = true };
            var w = new UsageWindow(4, future, 300);
            Check(Alerts.Evaluate(settings, s, w, "5-hour").Count() == 3, "Skipped threshold alerts");
            Check(!Alerts.Evaluate(settings, s, w, "5-hour").Any(), "Deduplicate alerts");
            Check(Alerts.Evaluate(settings, s, w with { Reset = future + 1 }, "5-hour").Count() == 3, "Rearm new quota window");
            settings.Notifications = false;
            Check(!Alerts.Evaluate(settings, s, w, "Weekly").Any(), "Notifications off");
            var presence = new AppPresenceState();
            Check(!presence.ShouldShow, "Hidden before app launch");
            Check(presence.Update(true) && presence.ShouldShow, "Show when app opens");
            presence.Hide(); Check(!presence.ShouldShow && !presence.Update(true), "Manual hiding persists while app open");
            presence.Restore(); Check(presence.ShouldShow, "Restore from tray");
            Check(presence.Update(false) && !presence.ShouldShow, "Hide when app closes");
            presence.Hide(); presence.Update(false); presence.Update(true); Check(presence.ShouldShow, "Rearm display on app relaunch");
            File.WriteAllText(args.Last(), $"PASS: {count} meaningful usage / threshold tests"); return 0;
        }
        catch (Exception e) { File.WriteAllText(args.Last(), "FAIL: " + e.Message); return 1; }
    }
}
