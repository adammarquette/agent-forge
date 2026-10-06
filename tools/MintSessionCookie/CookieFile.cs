using System.Security.AccessControl;
using System.Security.Principal;

namespace AgentForge.MintSessionCookie;

/// <summary>
/// Writes the cookie pair to the one file the user named, readable and writable only by them. That holds
/// when the file already existed too: a create-time mode alone would keep an older file's wider access.
/// </summary>
internal static class CookieFile
{
    public static async Task WriteAsync(string path, string pair)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var stream = OpenUserOnly(path);
        await using (stream.ConfigureAwait(false))
        {
            var writer = new StreamWriter(stream);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteAsync(pair).ConfigureAwait(false);
            }
        }
    }

    // Restricted after the truncating open and before a byte is written, so the pair never sits in a file
    // that still carries the old access.
    private static FileStream OpenUserOnly(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(path, options);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // No mode bits here. The 0600 equivalent is a protected DACL (nothing inherited from the directory)
                // whose only entry is the current user; an overwritten file would otherwise keep its old DACL.
                var security = new FileSecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
                // By path: a FileStream handle lacks WRITE_DAC on a file that already existed. Sharing modes do
                // not cover WRITE_DAC, so this works while the stream holds the file exclusively.
                new FileInfo(path).SetAccessControl(security);
            }
            else
            {
                File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
