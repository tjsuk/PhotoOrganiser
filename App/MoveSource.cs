using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhotoOrganiser;

// Windows can mark the same handle we read for deletion. Holding that handle without
// write or delete sharing prevents a concurrent replacement between reading and removal.
internal static class MoveSource
{
    private const uint GenericRead = 0x80000000;
    private const uint Delete = 0x00010000;
    private const uint ShareRead = 1;
    private const uint OpenExisting = 3;
    private const uint SequentialScan = 0x08000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const int FileDispositionInfo = 4;

    internal static FileStream Open(string path)
    {
        var handle = CreateFile(path, GenericRead | Delete, ShareRead, IntPtr.Zero, OpenExisting,
            SequentialScan | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = FileError("Could not open the original for moving");
            handle.Dispose();
            throw error;
        }

        try
        {
            if (File.GetAttributes(handle).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("The original is a link or cloud placeholder.");
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    // Windows removes the original when the handle closes after a successful disposition request.
    internal static void Remove(SafeFileHandle handle)
    {
        if (!SetFileInformationByHandle(handle, FileDispositionInfo, new Disposition { DeleteFile = 1 }, 1))
            throw FileError("Could not remove the original");
    }

    private static IOException FileError(string message)
    {
        var error = new Win32Exception(Marshal.GetLastPInvokeError());
        return new IOException($"{message}. {error.Message}", error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Disposition
    {
        public byte DeleteFile;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        in Disposition information, uint size);
}
