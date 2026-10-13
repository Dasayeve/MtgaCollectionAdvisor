using System.Security.Cryptography;
using System.Text;

namespace MtgaCollectionAdvisor.Core.Hosting;

/// <summary>
/// One running app per database (#119). A second launch finds the first by this name, asks it
/// to show its window and exits; a test copy on another database runs alongside.
/// </summary>
public static class SingleInstance
{
    /// <summary>
    /// A name for a mutex and an event, from the database's full path. "/" and "\" and letter
    /// case name the same file on Windows, so they give the same name (the spike started two
    /// copies on one database over a slash).
    /// </summary>
    public static string NameFor(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);

        var path = Path.GetFullPath(databasePath).Replace('/', '\\').ToLowerInvariant();
        return "MtgaDeckAdvisor." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16];
    }
}
