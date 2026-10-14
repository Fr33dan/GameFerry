using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Nanook.NKit;

namespace GameFerry.NKitHost
{
    /// <summary>
    /// Runs NKit.dll on behalf of the main application. Every line written to stdout is one JSON object:
    /// "info", "log", "progress", "result" or "error". The exit code is 0 on success and 1 on failure.
    /// </summary>
    internal static class Program
    {
        private static readonly object OutputLock = new object();
        private static TextWriter _output;

        private static int Main(string[] args)
        {
            _output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

            // Anything NKit or its dependencies print must not end up in the protocol stream.
            Console.SetOut(Console.Error);

            try
            {
                if (args.Length == 0)
                    throw new ArgumentException("Usage: GameFerry.NKitHost <info|convert|recover-test> [options]");

                Options options = Options.Parse(args.Skip(1).ToArray());
                WatchParent(options.Get("parent-pid"));

                switch (args[0])
                {
                    case "info":
                        return Info();
                    case "convert":
                        return Convert(options, false);
                    case "recover-test":
                        return Convert(options, true);
                    default:
                        throw new ArgumentException("Unknown command '" + args[0] + "'.");
                }
            }
            catch (Exception ex)
            {
                Emit(new Json().Add("t", "error").Add("message", Describe(ex)));
                return 1;
            }
        }

        /// <summary>Reads one path per line from stdin and reports the disc image(s) each one holds.</summary>
        private static int Info()
        {
            using (StreamReader input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
            {
                string path;
                while ((path = input.ReadLine()) != null)
                {
                    if (path.Length != 0)
                        Inspect(path);
                }
            }

            return 0;
        }

        private static void Inspect(string path)
        {
            SourceFile[] files;
            try
            {
                files = Scan(path);
            }
            catch (Exception ex)
            {
                EmitInfoFailure(path, Describe(ex));
                return;
            }

            if (files.Length == 0)
            {
                EmitInfoFailure(path, "No disc image found.");
                return;
            }

            foreach (SourceFile file in files)
            {
                try
                {
                    using (NStream stream = file.OpenNStream())
                    {
                        Emit(new Json()
                            .Add("t", "info")
                            .Add("path", path)
                            .Add("ok", true)
                            .Add("entry", file.IsArchive ? file.Name : null)
                            .Add("id", Clean(stream.Id6))
                            .Add("title", Clean(stream.Title))
                            .Add("discNo", stream.DiscNo)
                            .Add("version", stream.Version)
                            .Add("isWii", stream.IsWii)
                            .Add("isGameCube", stream.IsGameCube)
                            .Add("isNkit", stream.IsNkit)
                            .Add("isWbfs", stream.IsWbfs)
                            .Add("isGcz", stream.IsGcz)
                            .Add("updateRemoved", stream.IsNkit && stream.IsNkitUpdateRemoved)
                            .Add("sourceSize", stream.SourceSize)
                            .Add("imageSize", stream.ImageSize));
                    }
                }
                catch (Exception ex)
                {
                    EmitInfoFailure(path, Describe(ex));
                }
            }
        }

        private static void EmitInfoFailure(string path, string message)
        {
            Emit(new Json().Add("t", "info").Add("path", path).Add("ok", false).Add("message", message));
        }

        /// <summary>
        /// Converts a source image to a full ISO. With <paramref name="recover"/> it instead rebuilds the original
        /// disc from a scrubbed image in NKit's test mode (nothing is kept) to see whether it matches Redump.
        /// </summary>
        private static int Convert(Options options, bool recover)
        {
            string source = options.Require("source");
            string temp = options.Require("temp");
            string output = recover ? Path.Combine(temp, "recover-test.iso") : options.Require("out");

            Directory.CreateDirectory(temp);
            SourceFile file = Select(Scan(source), options.Get("entry"), source);

            Converter converter = new Converter(file, false);
            converter.LogMessage += (sender, e) =>
                Emit(new Json().Add("t", "log").Add("level", e.Type.ToString()).Add("message", e.Message));

            Stopwatch sinceProgress = Stopwatch.StartNew();
            converter.LogProgress += (sender, e) =>
            {
                // NKit reports progress far more often than anyone can watch.
                if (!e.IsStart && !e.IsComplete && sinceProgress.ElapsedMilliseconds < 100)
                    return;

                sinceProgress.Restart();
                Emit(new Json()
                    .Add("t", "progress")
                    .Add("start", e.IsStart)
                    .Add("complete", e.IsComplete)
                    .Add("pass", e.IsStart ? e.StartMessage : e.CompleteMessage)
                    .Add("value", e.Progress)
                    .Add("total", e.TotalProgress)
                    .Add("size", e.Size));
            };

            // The Converter built its settings from NKit.dll.config; point everything job-specific at this job.
            Settings settings = converter.Settings;
            settings.TempPath = temp;
            settings.EnableSummaryLog = false;
            settings.DeleteSource = false;
            settings.OutputLevel = 2;

            // With renaming off NKit would write the ISO beside the source; a mask with no tokens is a fixed path.
            settings.RedumpMatchRenameToMask = output;
            settings.CustomMatchRenameToMask = output;
            settings.MatchFailRenameToMask = output;

            if (options.Get("redump-dat") != null)
                settings.DatPathRedump = options.Get("redump-dat");
            if (options.Get("custom-dat") != null)
                settings.DatPathCustom = options.Get("custom-dat");
            if (options.Get("gametdb") != null)
                settings.DatPathNameGameTdb = options.Get("gametdb");
            if (options.Get("recovery") != null)
                settings.RecoveryFilesPath = options.Get("recovery");

            OutputResults results = recover
                ? converter.RecoverToIso(true, false, false, true)
                : converter.ConvertToIso(true, options.Has("full-verify"), false, false);

            bool ok = results != null
                && results.ValidateReadResult != VerifyResult.VerifyFailed
                && results.VerifyOutputResult != VerifyResult.VerifyFailed
                && results.VerifyOutputResult != VerifyResult.Error;
            if (!recover)
                ok = ok && results.OutputFileName != null && File.Exists(results.OutputFileName);

            Json json = new Json().Add("t", "result").Add("ok", ok);
            if (results != null)
            {
                json.Add("output", results.OutputFileName)
                    .Add("id", Clean(results.OutputId6))
                    .Add("title", Clean(results.OutputTitle))
                    .Add("outputSize", results.OutputSize)
                    .Add("crc", results.OutputCrc.ToString("X8"))
                    .Add("readResult", results.ValidateReadResult.ToString())
                    .Add("outputResult", results.VerifyOutputResult.ToString())
                    .Add("redumpMatch", results.RedumpInfo != null ? results.RedumpInfo.MatchType.ToString() : null)
                    .Add("redumpName", results.RedumpInfo != null ? results.RedumpInfo.MatchName : null)
                    .Add("recoverable", results.IsRecoverable)
                    .Add("seconds", results.ProcessingTime.TotalSeconds)
                    .Add("message", results.ErrorMessage);
            }

            Emit(json);
            return ok ? 0 : 1;
        }

        private static SourceFile[] Scan(string path)
        {
            // A split WBFS set has to be handed to NKit as a mask covering all its parts.
            if (path.EndsWith(".wbfs", StringComparison.OrdinalIgnoreCase)
                && File.Exists(path.Substring(0, path.Length - 1) + "1"))
            {
                path = path.Substring(0, path.Length - 2) + "*";
            }

            return SourceFiles.Scan(new[] { path }, false);
        }

        private static SourceFile Select(SourceFile[] files, string entry, string source)
        {
            SourceFile file = entry == null
                ? files.FirstOrDefault()
                : files.FirstOrDefault(f => string.Equals(f.Name, entry, StringComparison.OrdinalIgnoreCase));
            if (file == null)
                throw new FileNotFoundException("No disc image found in '" + source + "'.");
            return file;
        }

        /// <summary>Ends this process if the application that started it goes away.</summary>
        private static void WatchParent(string parentPid)
        {
            int pid;
            if (!int.TryParse(parentPid, out pid))
                return;

            Thread watcher = new Thread(() =>
            {
                try
                {
                    Process.GetProcessById(pid).WaitForExit();
                }
                catch (ArgumentException)
                {
                    // Already gone.
                }

                Environment.Exit(3);
            });
            watcher.IsBackground = true;
            watcher.Start();
        }

        private static string Clean(string value)
        {
            return value == null ? null : value.Trim('\0', ' ');
        }

        private static string Describe(Exception ex)
        {
            AggregateException aggregate = ex as AggregateException;
            if (aggregate != null)
            {
                IList<Exception> inner = aggregate.Flatten().InnerExceptions;
                ex = inner.FirstOrDefault(e => e is HandledException) ?? inner.FirstOrDefault() ?? ex;
            }

            HandledException handled = ex as HandledException;
            return handled != null ? handled.FriendlyErrorMessage : ex.Message;
        }

        private static void Emit(Json json)
        {
            lock (OutputLock)
                _output.WriteLine(json.ToString());
        }
    }
}
