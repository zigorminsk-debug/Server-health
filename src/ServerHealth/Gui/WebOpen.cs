using System.Diagnostics;

namespace ServerHealth.Gui;

/// <summary>
/// Открытие локального html/URL в браузере с запасными вариантами:
/// на серверных ОС ассоциация .html часто отсутствует или указывает
/// на отключённый Internet Explorer, и ShellExecute просто падает.
/// </summary>
internal static class WebOpen
{
    /// <summary>Открывает путь/URL; false — ни один способ не сработал.</summary>
    public static bool Open(string pathOrUrl)
    {
        // 1) системная ассоциация (обычный случай)
        try
        {
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
            return true;
        }
        catch { }

        string arg;
        try
        {
            arg = "\"" + (pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? pathOrUrl
                : new Uri(pathOrUrl).AbsoluteUri) + "\"";
        }
        catch { arg = "\"" + pathOrUrl + "\""; }

        // 2) Edge (есть на всех современных Windows/Windows Server) → 3) Chrome → 4) Firefox
        if (TryExe(@"Microsoft\Edge\Application\msedge.exe", arg)) return true;
        if (TryExe(@"Google\Chrome\Application\chrome.exe", arg)) return true;
        if (TryExe(@"Mozilla Firefox\firefox.exe", arg)) return true;

        // 5) обработчик протоколов через rundll32 — работает даже без ассоциации расширения
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe",
                "url.dll,FileProtocolHandler " + arg) { UseShellExecute = true });
            return true;
        }
        catch { }

        return false;
    }

    /// <summary>Запуск браузера по типовому пути установки (ProgramFiles / ProgramFiles(x86) / LocalAppData).</summary>
    private static bool TryExe(string rel, string args)
    {
        foreach (var root in new[]
        {
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("LocalAppData")
        })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string exe = Path.Combine(root!, rel);
            if (!File.Exists(exe)) continue;
            try
            {
                Process.Start(new ProcessStartInfo("\"" + exe + "\"", args) { UseShellExecute = true });
                return true;
            }
            catch { }
        }
        return false;
    }
}
