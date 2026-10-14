namespace GameFerry.Core.Transfer;

/// <summary>The local folder rebuilt ISOs are staged in, one sub-folder per game being transferred.</summary>
public sealed class WorkDirectory
{
    private const string JobPrefix = "job-";
    private const string LockFileName = ".lock";

    public WorkDirectory(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public long FreeBytes
    {
        get
        {
            try
            {
                return new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                return long.MaxValue;
            }
        }
    }

    public WorkJob CreateJob()
    {
        string path = Path.Combine(Root, JobPrefix + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);
        FileStream jobLock = new(Path.Combine(path, LockFileName), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        return new WorkJob(path, jobLock);
    }

    /// <summary>
    /// Deletes job folders left behind by a run that crashed or was killed. Folders whose lock file is still
    /// held belong to another running instance and are left alone.
    /// </summary>
    public void CleanStale()
    {
        foreach (string path in Directory.EnumerateDirectories(Root, JobPrefix + "*"))
        {
            try
            {
                string lockPath = Path.Combine(path, LockFileName);
                if (File.Exists(lockPath))
                    new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();

                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>One game's staging folder; disposing it deletes the folder and everything in it.</summary>
public sealed class WorkJob : IDisposable
{
    private readonly FileStream _lock;

    internal WorkJob(string path, FileStream jobLock)
    {
        Path = path;
        _lock = jobLock;
    }

    public string Path { get; }

    public void Dispose()
    {
        _lock.Dispose();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Left for CleanStale on the next run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
