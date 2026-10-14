using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace GameFerry.Core.NKit;

/// <summary>
/// The NKit helper process is carried inside this assembly as resources. It has to exist as real files to be
/// run (and NKit reads its settings from a file beside its DLL), so it is unpacked to a per-user folder.
/// </summary>
internal static class EmbeddedNKitHost
{
    private const string ResourcePrefix = "nkithost/";
    private const string HostFileName = "GameFerry.NKitHost.exe";

    private static readonly Lock Gate = new();
    private static string? _hostPath;

    /// <summary>Unpacks the helper if it isn't there already and returns the path of its executable.</summary>
    public static string Unpack()
    {
        lock (Gate)
        {
            if (_hostPath is not null && File.Exists(_hostPath))
                return _hostPath;

            List<(string Name, byte[] Content)> files = ReadEmbeddedFiles();

            // The folder is named after its contents, so a new version of the application gets a folder of its
            // own and never replaces files an older copy might be running at that moment.
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach ((string name, byte[] content) in files)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(name));
                hash.AppendData(content);
            }

            string folder = Path.Combine(AppPaths.NKitHostFolder, Convert.ToHexString(hash.GetHashAndReset(), 0, 8));
            Directory.CreateDirectory(folder);
            foreach ((string name, byte[] content) in files)
                WriteIfMissing(Path.Combine(folder, name), content);

            return _hostPath = Path.Combine(folder, HostFileName);
        }
    }

    private static List<(string Name, byte[] Content)> ReadEmbeddedFiles()
    {
        Assembly assembly = typeof(EmbeddedNKitHost).Assembly;
        List<(string, byte[])> files = [];
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            byte[] content = new byte[stream.Length];
            stream.ReadExactly(content);
            files.Add((resource[ResourcePrefix.Length..], content));
        }

        if (!files.Any(file => file.Item1 == HostFileName))
            throw new InvalidOperationException("The NKit helper is not embedded in this build.");
        return files;
    }

    private static void WriteIfMissing(string path, byte[] content)
    {
        if (IsInPlace(path, content))
            return;

        // Written under another name and moved into place, so a second copy of the application starting at the
        // same moment never sees half a file.
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) when (IsInPlace(path, content))
        {
            // That other copy got there first.
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static bool IsInPlace(string path, byte[] content) =>
        File.Exists(path) && new FileInfo(path).Length == content.Length;
}
