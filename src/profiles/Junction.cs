using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Modificus.Curator.Profiles;

/// <summary>
/// Creates an NTFS directory <b>junction</b> (<c>mklink /J</c>) at
/// <paramref name="linkPath"/> pointing to <paramref name="targetPath"/>. Unlike
/// a symbolic link, a junction requires no privilege on Windows (no Developer
/// Mode, no admin, no <c>SeCreateSymbolicLinkPrivilege</c>), which is why mod
/// staging uses it on Windows. NTFS-only and local-volume only; the repository
/// and profiles are local NTFS in practice.
/// </summary>
/// <remarks>
/// Implemented via the <c>FSCTL_SET_REPARSE_POINT</c> device control on an open
/// directory handle. The reparse buffer layout mirrors what <c>mklink /J</c>
/// writes (verified by reading one back): <c>SubstituteName</c> =
/// <c>\??\&lt;abspath&gt;</c>, <c>PrintName</c> = <c>&lt;abspath&gt;</c> (no
/// prefix), each name followed by a wide-char null terminator; offsets in bytes.
/// Constants are the Windows SDK values (<c>winnt.h</c> / <c>ntifs.h</c>).
/// Windows-only: the staging link seam selects the symlink implementation on
/// Linux (see <see cref="ServiceCollectionExtensions.AddProfiles"/>).
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class Junction
{
    // Windows SDK constants (winnt.h / ntifs.h):
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;  // mount point / junction tag
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;     // DeviceIoControl code
    private const uint GENERIC_WRITE = 0x40000000;               // dwDesiredAccess
    private const uint OPEN_EXISTING = 3;                        // dwCreationDisposition
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000; // open the reparse point, don't follow it
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;  // required to open a directory

    /// <summary>
    /// Creates a junction at <paramref name="linkPath"/> pointing to
    /// <paramref name="targetPath"/>. The <paramref name="targetPath"/> must
    /// already exist; the <paramref name="linkPath"/> is created here as an empty
    /// directory, then turned into a junction. Throws
    /// <see cref="Win32Exception"/> (carrying the last P/Invoke error) on failure;
    /// the staging call site + launch façade let the raised built-in exception
    /// propagate as-is (the symlink path's <see cref="IOException"/> likewise
    /// propagates unwrapped), so the actual runtime/OS error reaches the caller.
    /// </summary>
    internal static void Create(string linkPath, string targetPath)
    {
        Directory.CreateDirectory(linkPath); // the future junction, as an empty dir

        string substitute = @"\??\" + targetPath;              // NT object-path form
        byte[] subBytes = Encoding.Unicode.GetBytes(substitute);
        byte[] printBytes = Encoding.Unicode.GetBytes(targetPath); // no \??\ prefix
        byte[] nulWide = { 0, 0 };

        int subLen = subBytes.Length;
        int printOff = subBytes.Length + 2;                    // after substitute + its null terminator
        int printLen = printBytes.Length;
        int pathBufferLen = subBytes.Length + 2 + printBytes.Length + 2; // null term after each name
        int dataLen = 8 + pathBufferLen;                        // 4 name USHORTs + path buffer
        byte[] buf = new byte[8 + dataLen];                     // common header (8) + data
        BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(buf, 0); // ReparseTag (mount point)
        BitConverter.GetBytes((ushort)dataLen).CopyTo(buf, 4);            // ReparseDataLength
        BitConverter.GetBytes((ushort)0).CopyTo(buf, 6);                  // Reserved
        BitConverter.GetBytes((ushort)0).CopyTo(buf, 8);                  // SubstituteNameOffset
        BitConverter.GetBytes((ushort)subLen).CopyTo(buf, 10);            // SubstituteNameLength
        BitConverter.GetBytes((ushort)printOff).CopyTo(buf, 12);          // PrintNameOffset
        BitConverter.GetBytes((ushort)printLen).CopyTo(buf, 14);          // PrintNameLength
        int p = 16;
        Array.Copy(subBytes, 0, buf, p, subBytes.Length); p += subBytes.Length;
        Array.Copy(nulWide, 0, buf, p, 2); p += 2;
        Array.Copy(printBytes, 0, buf, p, printBytes.Length); p += printBytes.Length;
        Array.Copy(nulWide, 0, buf, p, 2);

        // The P/Invoke calls throw Win32Exception (carrying the last SetLastError
        // value) on failure. It is propagated as-is: the staging layer never
        // silently copies, and the raised built-in exception's message (a
        // runtime/OS error, not a string we invented) is surfaced to the user
        // after the localized framing in the launch alert.
        using var h = CreateFileW(linkPath, GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid)
        {
            throw new Win32Exception(); // captures the last P/Invoke error (SetLastError)
        }
        if (!DeviceIoControl(h, FSCTL_SET_REPARSE_POINT, buf, (uint)buf.Length, null, 0, out _, IntPtr.Zero))
        {
            throw new Win32Exception();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, byte[]? lpInBuffer, uint nInBufferSize,
        byte[]? lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
}
