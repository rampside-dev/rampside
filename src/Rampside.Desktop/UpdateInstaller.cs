using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Rampside.Desktop;

/// <summary>
/// Runs a downloaded Rampside MSI: a small PowerShell script waits until this process has closed (so the
/// installer can replace its files), runs msiexec, then starts Rampside again. Windows asks for administrator
/// permission, since Rampside is installed for all users.
/// </summary>
public static class UpdateInstaller
{
    // The MSI writes its folder here (installer/Package.wxs); builds from source have no such entry.
    private const string InstallKey = @"Software\Rampside";
    private const string InstallFolderValue = "InstallFolder";

    private const string Script = """
        # Written by Rampside to install an update. Waits for Rampside to close, runs the installer, starts Rampside again.
        param([int]$RampsidePid, [string]$Msi, [string]$Log, [string]$App, [switch]$Wizard)
        Wait-Process -Id $RampsidePid -Timeout 60 -ErrorAction SilentlyContinue
        $ui = if ($Wizard) { '' } else { '/passive' }
        $arguments = '/i "{0}" {1} /norestart /l*v "{2}"' -f $Msi, $ui, $Log
        $install = Start-Process -FilePath msiexec.exe -ArgumentList $arguments -Wait -PassThru
        # Installed (0, or 3010 = reboot needed): start the installed copy, wherever the user put it.
        # Cancelled or failed: start the Rampside that was running before.
        if ($install.ExitCode -in 0, 3010) {
            $folder = (Get-ItemProperty -Path 'HKLM:\Software\Rampside' -Name InstallFolder -ErrorAction SilentlyContinue).InstallFolder
            if ($folder -and (Test-Path -LiteralPath (Join-Path $folder 'Rampside.exe'))) { $App = Join-Path $folder 'Rampside.exe' }
        }
        Start-Process -FilePath $App
        """;

    /// <summary>
    /// True when this process runs from the folder the MSI installed Rampside into. Then an update installs in
    /// place with a progress bar only; any other copy (e.g. a build from source) gets the full installer wizard.
    /// </summary>
    public static bool IsInstalledCopy
    {
        get
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = machine.OpenSubKey(InstallKey);
                return key?.GetValue(InstallFolderValue) is string folder && !string.IsNullOrWhiteSpace(folder)
                    && string.Equals(NormalizeFolder(folder), NormalizeFolder(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Starts the script that installs <paramref name="msiPath"/> once Rampside has closed. Close Rampside right after.
    /// <paramref name="wizard"/> shows the full installer (license, folder) instead of a progress bar only.
    /// </summary>
    public static void Start(string msiPath, bool wizard)
    {
        var folder = Path.GetDirectoryName(msiPath)!;
        var script = Path.Combine(folder, "install-update.ps1");
        File.WriteAllText(script, Script);

        var app = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Rampside.exe");
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
            "-RampsidePid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-Msi", msiPath,
            "-Log", Path.ChangeExtension(msiPath, ".log"),
            "-App", app,
        })
        {
            start.ArgumentList.Add(argument);
        }

        if (wizard)
        {
            start.ArgumentList.Add("-Wizard");
        }

        using var process = Process.Start(start) ?? throw new IOException("PowerShell could not be started.");
    }

    private static string NormalizeFolder(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
}
