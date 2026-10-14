using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GameFerry.Core.NKit;

/// <summary>A conversion for the NKit host to carry out.</summary>
/// <param name="Source">Image or archive to read.</param>
/// <param name="Entry">Image inside the archive, when there is more than one.</param>
/// <param name="OutputIso">Where the full ISO is to be written; must be on the same volume as <paramref name="WorkDirectory"/>.</param>
/// <param name="WorkDirectory">Folder for NKit's temporary files.</param>
public sealed record NKitJob(string Source, string? Entry, string OutputIso, string WorkDirectory, NKitData Data, bool FullVerify = false);

/// <summary>
/// Runs NKit in its own process (<c>GameFerry.NKitHost.exe</c>). NKit.dll is a .NET Framework library with
/// process-wide settings, so it is kept out of this process; cancelling simply ends the host.
/// </summary>
public sealed class NKitHostClient
{
    public NKitHostClient(string hostPath)
    {
        HostPath = hostPath;
    }

    public string HostPath { get; }

    /// <summary>
    /// Makes the helper process ready to run by unpacking it from this assembly. Returns null if it could not
    /// be written to <see cref="AppPaths.NKitHostFolder"/>.
    /// </summary>
    public static NKitHostClient? TryCreate()
    {
        try
        {
            return new NKitHostClient(EmbeddedNKitHost.Unpack());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>What to tell the user when <see cref="TryCreate"/> came back empty.</summary>
    public static string UnavailableMessage => $"The NKit helper could not be unpacked to {AppPaths.NKitHostFolder}.";

    /// <summary>Identifies the disc image in each file; only headers are read, even inside archives.</summary>
    public async Task InspectAsync(IReadOnlyList<string> paths, Action<NKitImageInfo> onInfo, CancellationToken cancellationToken)
    {
        await RunAsync(
            ["info"],
            workDirectory: null,
            input: paths,
            message =>
            {
                if (NKitHostProtocol.Type(message) == "info")
                    onInfo(NKitHostProtocol.ToImageInfo(message));
            },
            log: null,
            cancellationToken);
    }

    /// <summary>Rebuilds the full ISO from any image NKit can read (NKit, GCZ, WBFS, or one inside an archive).</summary>
    public Task<NKitResult> ConvertToIsoAsync(NKitJob job, IProgress<NKitProgress>? progress, Action<string>? log, CancellationToken cancellationToken) =>
        RunJobAsync("convert", job, progress, log, cancellationToken);

    /// <summary>
    /// Has NKit rebuild the original disc from a scrubbed image without keeping the result, to learn whether
    /// it still matches Redump. This proves the scrubbed image lost nothing but padding.
    /// </summary>
    public Task<NKitResult> RecoverTestAsync(NKitJob job, IProgress<NKitProgress>? progress, Action<string>? log, CancellationToken cancellationToken) =>
        RunJobAsync("recover-test", job, progress, log, cancellationToken);

    private async Task<NKitResult> RunJobAsync(string command, NKitJob job, IProgress<NKitProgress>? progress, Action<string>? log, CancellationToken cancellationToken)
    {
        List<string> arguments = [command, "--source", job.Source, "--temp", job.WorkDirectory];
        if (command == "convert")
            arguments.AddRange(["--out", job.OutputIso]);
        AddOption(arguments, "--entry", job.Entry);
        AddOption(arguments, "--redump-dat", job.Data.RedumpDat);
        AddOption(arguments, "--custom-dat", job.Data.CustomDat);
        AddOption(arguments, "--gametdb", job.Data.GameTdb);
        AddOption(arguments, "--recovery", job.Data.RecoveryDirectory);
        if (job.FullVerify)
            arguments.Add("--full-verify");

        NKitResult? result = null;
        string? error = null;
        int exitCode = await RunAsync(
            arguments,
            job.WorkDirectory,
            input: null,
            message =>
            {
                switch (NKitHostProtocol.Type(message))
                {
                    case "progress":
                        progress?.Report(NKitHostProtocol.ToProgress(message));
                        break;
                    case "log":
                        if (NKitHostProtocol.String(message, "message") is { Length: > 0 } line)
                            log?.Invoke(line);
                        break;
                    case "result":
                        result = NKitHostProtocol.ToResult(message);
                        break;
                    case "error":
                        error = NKitHostProtocol.String(message, "message");
                        break;
                }
            },
            log,
            cancellationToken);

        return result ?? new NKitResult { Ok = false, Message = error ?? $"The NKit host ended unexpectedly (exit code {exitCode})." };
    }

    private async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        string? workDirectory,
        IReadOnlyList<string>? input,
        Action<JsonElement> onMessage,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(HostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

        if (workDirectory is not null)
        {
            // NKit names its temporary files via Path.GetTempFileName; keep those in the job folder too.
            startInfo.Environment["TMP"] = workDirectory;
            startInfo.Environment["TEMP"] = workDirectory;
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the NKit host.");
        await using CancellationTokenRegistration killOnCancel = cancellationToken.Register(() => Kill(process));

        Task errors = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (line.Length > 0)
                    log?.Invoke(line);
            }
        });

        Task writes = Task.Run(async () =>
        {
            try
            {
                foreach (string line in input ?? [])
                    await process.StandardInput.WriteLineAsync(line);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The host went away (cancelled or crashed); the read loop reports what happened.
            }
        });

        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                onMessage(document.RootElement);
            }
            catch (JsonException)
            {
                log?.Invoke(line);
            }
        }

        await Task.WhenAll(errors, writes);
        await process.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    private static void AddOption(List<string> arguments, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            arguments.AddRange([name, value]);
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
}
