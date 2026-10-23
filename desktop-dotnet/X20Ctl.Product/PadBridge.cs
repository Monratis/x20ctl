using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace X20Ctl.Product;

public sealed record TelemetryData(string? Controller, string? Firmware, string? Battery, bool Charging, string? Address, string RawText);

/// <summary>
/// Optional on-demand reader for controller telemetry over Bluetooth LE.
/// On Windows, communicates natively via WinRT Bluetooth LE with zero Python required.
/// Never required: playing over 2.4 GHz USB receiver and PC Mode work fully without Bluetooth.
/// </summary>
public static class PadBridge
{
    public sealed record Result(bool Ran, bool Ok, string Text, TelemetryData? Telemetry = null);

    static readonly (string File, string Prefix)[] Launchers = [
        ("x20", ""),
        ("python3", "-X utf8 -m x20ctl "),
        ("python", "-X utf8 -m x20ctl "),
        ("py", "-X utf8 -m x20ctl ")
    ];
    static readonly Regex Ansi = new("\u001b\\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    public static async Task<Result> Status()
    {
#if WINDOWS
        // Native Windows WinRT Bluetooth LE directly in C# (Zero Python required!)
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063))
        {
            return await NativeBleClient.QueryAsync();
        }
        return new(true, false, "Bluetooth LE requires Windows 10 build 15063 or newer.");
#else
        var res = await Run("status");
        if (res.Ok && !string.IsNullOrEmpty(res.Text))
        {
            var telemetry = ParseTelemetry(res.Text);
            return res with { Telemetry = telemetry };
        }
        return res;
#endif
    }

    public static TelemetryData ParseTelemetry(string text)
    {
        string? firmware = null;
        string? battery = null;
        bool charging = false;
        string? address = null;
        string? controller = null;

        var mFw = Regex.Match(text, @"(?m)^\s{1,4}firmware\s+([^\r\n]+)", RegexOptions.IgnoreCase);
        if (mFw.Success) firmware = mFw.Groups[1].Value.Trim();

        var mBat = Regex.Match(text, @"(?m)^\s{1,4}battery\s+([^\r\n]+)", RegexOptions.IgnoreCase);
        if (mBat.Success)
        {
            string rawBat = mBat.Groups[1].Value.Trim();
            charging = rawBat.Contains("charging", StringComparison.OrdinalIgnoreCase);
            var mLevel = Regex.Match(rawBat, @"(\d+/\d+)");
            if (mLevel.Success)
            {
                battery = mLevel.Groups[1].Value + (charging ? " (Charging)" : "");
            }
            else
            {
                battery = rawBat;
            }
        }

        var mAddr = Regex.Match(text, @"(?m)^\s{1,4}address\s+([0-9A-Fa-f:]+)", RegexOptions.IgnoreCase);
        if (mAddr.Success) address = mAddr.Groups[1].Value.Trim();

        var mCtrl = Regex.Match(text, @"(?m)^\s{1,4}controller\s+([^\r\n]+)", RegexOptions.IgnoreCase);
        if (mCtrl.Success) controller = mCtrl.Groups[1].Value.Trim();

        return new TelemetryData(controller, firmware, battery, charging, address, text);
    }

    static async Task<Result> Run(string command)
    {
        var candidates = new List<(string File, string Prefix)>(Launchers);
        string venvPy = Path.Combine(AppContext.BaseDirectory, ".venv", "Scripts", "python.exe");
        if (File.Exists(venvPy)) candidates.Insert(0, (venvPy, "-X utf8 -m x20ctl "));

        foreach (var (file, prefix) in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(file, prefix + command)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                psi.EnvironmentVariables["PYTHONUTF8"] = "1";
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                using var p = Process.Start(psi);
                if (p == null) continue;
                var output = p.StandardOutput.ReadToEndAsync();
                var error = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(); } catch { }
                    return new(true, false, "The controller didn't answer within 15 seconds. Is Bluetooth on and the controller awake?");
                }
                string text = Ansi.Replace(await output, "").Trim(), err = Ansi.Replace(await error, "").Trim();
                if (p.ExitCode != 0 && err.Contains("No module named", StringComparison.Ordinal)) continue;

                string outText = text.Length > 0 ? text : err;
                if (text.Length > 0 && err.Length > 0 && !text.Contains(err, StringComparison.Ordinal))
                    outText = text + "\n" + err;

                return new(true, p.ExitCode == 0, outText);
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return new(false, false, "The X20CTL Bluetooth client is not found in PATH.");
    }
}
