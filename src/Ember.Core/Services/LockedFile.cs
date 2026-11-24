namespace Ember.Core.Services;

/// <summary>A file several processes append to and read, one at a time.</summary>
public static class LockedFile
{
    /// <summary>
    /// Opens <paramref name="file"/> with an exclusive lock, waiting up to 5 seconds while another process holds it, and passes it to
    /// <paramref name="use"/>. A file it creates gets <paramref name="createMode"/>, or the platform's default when null.
    /// </summary>
    public static T Use<T>(string file, FileMode mode, FileAccess access, Func<FileStream, T> use, UnixFileMode? createMode = null)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = FileShare.None };
        if (createMode is { } unixMode && !OperatingSystem.IsWindows()) options.UnixCreateMode = unixMode;
        for (var attempt = 1; ; attempt++)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(file, options);
            }
            catch (IOException e) when (e is not (FileNotFoundException or DirectoryNotFoundException) && attempt < 50)
            {
                Thread.Sleep(100);
                continue;
            }
            using (stream) return use(stream);
        }
    }
}
