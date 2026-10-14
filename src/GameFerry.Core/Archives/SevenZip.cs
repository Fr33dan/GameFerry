using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameFerry.Core.Archives;

/// <summary>
/// Extracts archive entries with an installed 7-Zip. NKit can read archives itself, but its managed
/// decompressor is several times slower than 7-Zip's, which dominates the time taken for large games.
/// </summary>
public sealed partial class SevenZip
{
    private const string ExeName = "7z.exe";

    private SevenZip(string exePath)
    {
        ExePath = exePath;
    }

    public string ExePath { get; }

    /// <summary>Finds 7z.exe from 7-Zip's registry entry, its default install folder, or the PATH.</summary>
    public static SevenZip? TryLocate()
    {
        List<string?> folders = [];
        if (OperatingSystem.IsWindows())
        {
            folders.Add(InstallFolderFromRegistry(RegistryView.Registry64));
            folders.Add(InstallFolderFromRegistry(RegistryView.Registry32));
        }

        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip"));
        folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        foreach (string? folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;

            string candidate = Path.Combine(folder.Trim(), ExeName);
            if (File.Exists(candidate))
                return new SevenZip(candidate);
        }

        return null;
    }

    /// <summary>Extracts one file from an archive into a folder and returns the extracted file's path.</summary>
    /// <param name="entryName">File name of the entry (without any folder it has inside the archive).</param>
    /// <param name="progress">Receives the fraction extracted, 0 to 1.</param>
    public async Task<string> ExtractAsync(string archivePath, string entryName, string outputDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // e = extract without folder structure; -r = match the name at any depth;
        // -bsp1/-bso0/-bse2 = progress on stdout, no file listing, errors on stderr.
        foreach (string argument in (string[])["e", "-y", "-r", "-bsp1", "-bso0", "-bse2", "-o" + outputDirectory, "--", archivePath, entryName])
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo) ?? throw new IOException("Could not start 7-Zip.");
        await using CancellationTokenRegistration killOnCancel = cancellationToken.Register(() => Kill(process));

        Task<string> errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

        // Progress arrives as "  42% - name" fragments separated by carriage returns or backspaces.
        char[] buffer = new char[256];
        StringBuilder fragment = new();
        int read;
        while ((read = await process.StandardOutput.ReadAsync(buffer, CancellationToken.None)) > 0)
        {
            foreach (char c in buffer.AsSpan(0, read))
            {
                if (c is not ('\r' or '\n' or '\b'))
                {
                    fragment.Append(c);
                    continue;
                }

                if (PercentPattern().Match(fragment.ToString()) is { Success: true } match)
                    progress?.Report(int.Parse(match.Groups[1].Value) / 100.0);
                fragment.Clear();
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();

        string extracted = Path.Combine(outputDirectory, entryName);
        if (process.ExitCode != 0 || !File.Exists(extracted))
        {
            string detail = (await errors).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(line => line.Length > 0) ?? "no details";
            throw new IOException($"7-Zip could not extract '{entryName}' from {Path.GetFileName(archivePath)}: {detail} (exit code {process.ExitCode}).");
        }

        progress?.Report(1);
        return extracted;
    }

    [SupportedOSPlatform("windows")]
    private static string? InstallFolderFromRegistry(RegistryView view)
    {
        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using RegistryKey? key = machine.OpenSubKey(@"SOFTWARE\7-Zip");
            return key?.GetValue("Path") as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already exited.
        }
    }

    [GeneratedRegex(@"^\s*(\d{1,3})%")]
    private static partial Regex PercentPattern();
}
