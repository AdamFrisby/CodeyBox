using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeyBox.Sandbox.Incus;

internal static partial class IncusSafeFile
{
    private const int OpenReadOnly = 0;
    private const int OpenWriteOnly = 1;
    private const int OpenReadWrite = 2;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenNonBlock = 0x800;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int OpenPath = 0x200000;
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x000007ff;
    private const ushort FileTypeMask = 0xF000;
    private const ushort DirectoryFileType = 0x4000;
    private const ushort RegularFileType = 0x8000;
    private const ushort SymbolicLinkFileType = 0xA000;
    private const int AlreadyExists = 17;
    private const int WouldBlock = 11;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int LockUnlock = 8;

    internal static bool TryCreateDirectoryExclusive(string path)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The Incus provider requires Linux directory creation semantics.");
        const uint ownerOnlyMode = 0x1C0; // 0700
        if (CreateDirectory(path, ownerOnlyMode) == 0)
            return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == AlreadyExists)
            return false;
        throw new IOException("Unable to create the Incus staging root atomically.", new Win32Exception(error));
    }

    internal static FileStream OpenReadNoFollow(string path)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The Incus provider requires Linux no-follow file opening.");
        var descriptor = Open(path, OpenReadOnly | OpenNonBlock | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException("Unable to open isolation-snapshot source without following links.", new Win32Exception(error));
        }
        var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        try
        {
            EnsureRegularFile(handle);
            return new FileStream(handle, FileAccess.Read, bufferSize: 64 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static FileStream OpenOrCreatePrivateLeaseNoFollow(string path)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The Incus provider requires Linux lease-file semantics.");
        const uint ownerReadWriteMode = 0x180; // 0600
        var descriptor = OpenWithMode(
            path,
            OpenReadWrite | OpenCreate | OpenNoFollow | OpenCloseOnExec,
            ownerReadWriteMode);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException("Unable to open the private Incus provisioning lease without following links.", new Win32Exception(error));
        }
        SafeFileHandle? handle = new((nint)descriptor, ownsHandle: true);
        try
        {
            var status = ReadStatus(handle, RegularFileType, "private Incus provisioning lease");
            var identity = IncusHostIdentity.GetEffectiveIdentity();
            if (status.UserId != identity.UserId || status.GroupId != identity.GroupId)
            {
                throw new InvalidOperationException(
                    "Refusing an Incus provisioning lease owned by another host identity.");
            }
            if (SetFileMode(descriptor, ownerReadWriteMode) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException("Unable to set the private Incus provisioning lease mode.", new Win32Exception(error));
            }
            var result = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
            handle = null;
            return result;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal static bool TryAcquireExclusiveLease(FileStream lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (AcquireFileLock(
                lease.SafeFileHandle.DangerousGetHandle().ToInt32(),
                LockExclusive | LockNonBlocking) == 0)
        {
            return true;
        }
        var error = Marshal.GetLastPInvokeError();
        if (error == WouldBlock)
            return false;
        throw new IOException("Unable to acquire the private Incus provisioning lease.", new Win32Exception(error));
    }

    internal static void ReleaseExclusiveLease(FileStream lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (AcquireFileLock(
                lease.SafeFileHandle.DangerousGetHandle().ToInt32(),
                LockUnlock) == 0)
        {
            return;
        }
        var error = Marshal.GetLastPInvokeError();
        throw new IOException("Unable to release the private Incus provisioning lease.", new Win32Exception(error));
    }

    /// <summary>
    /// Pins the exact directory inode reached by an already-canonical host path.
    /// Each path component is opened relative to its pinned parent with
    /// <c>O_NOFOLLOW</c>, so a concurrent symbolic-link substitution cannot be
    /// accepted between a distant canonicalization check and the Incus device
    /// sink.
    /// </summary>
    internal static IncusPinnedDirectory PinDirectoryNoFollow(string path)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The Incus provider requires Linux no-follow directory opening.");
        var fullPath = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(fullPath))
            throw new ArgumentException("The pinned directory path must be absolute.", nameof(path));

        SafeFileHandle? current = null;
        try
        {
            current = OpenDirectoryHandle("/");
            foreach (var segment in fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var descriptor = OpenAt(
                    current.DangerousGetHandle().ToInt32(),
                    segment,
                    OpenPath | OpenDirectory | OpenNoFollow | OpenCloseOnExec);
                if (descriptor < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    throw new IOException(
                        "Unable to pin a host mount directory without following links.",
                        new Win32Exception(error));
                }
                var next = new SafeFileHandle((nint)descriptor, ownsHandle: true);
                current.Dispose();
                current = next;
            }

            var status = ReadStatus(current, DirectoryFileType, "host mount directory");
            var result = new IncusPinnedDirectory(
                current,
                status.Identity,
                status.Mode,
                status.UserId,
                status.GroupId);
            current = null;
            return result;
        }
        finally
        {
            current?.Dispose();
        }
    }

    internal static void EnsurePinnedDirectoryMatches(string path, IncusPinnedDirectory expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        using var current = PinDirectoryNoFollow(path);
        if (current.Identity != expected.Identity)
        {
            throw new IOException(
                "The authorized host mount directory changed identity before Incus could attach it.");
        }
    }

    internal static IEnumerable<string> EnumerateChildNames(IncusPinnedDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return Directory.EnumerateFileSystemEntries(directory.DescriptorPath)
            .Select(Path.GetFileName)
            .Select(name => ValidateEntryName(name));
    }

    internal static IncusDirectoryEntryMetadata InspectChildNoFollow(
        IncusPinnedDirectory directory,
        string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        name = ValidateEntryName(name);
        if (Statx(
                directory.Descriptor,
                name,
                AtSymlinkNoFollow,
                StatxBasicStats,
                out var status) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException("Unable to inspect a pinned snapshot entry without following links.", new Win32Exception(error));
        }
        var kind = (status.Mode & FileTypeMask) switch
        {
            DirectoryFileType => IncusDirectoryEntryKind.Directory,
            RegularFileType => IncusDirectoryEntryKind.RegularFile,
            SymbolicLinkFileType => IncusDirectoryEntryKind.SymbolicLink,
            _ => IncusDirectoryEntryKind.Unsupported,
        };
        return new IncusDirectoryEntryMetadata(kind, ToUnixFileMode(status.Mode));
    }

    internal static IncusPinnedDirectory OpenChildDirectoryNoFollow(
        IncusPinnedDirectory directory,
        string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        name = ValidateEntryName(name);
        var descriptor = OpenAt(
            directory.Descriptor,
            name,
            OpenPath | OpenDirectory | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException("Unable to open a pinned snapshot directory without following links.", new Win32Exception(error));
        }
        SafeFileHandle? handle = new((nint)descriptor, ownsHandle: true);
        try
        {
            var status = ReadStatus(handle, DirectoryFileType, "pinned snapshot directory");
            var result = new IncusPinnedDirectory(
                handle,
                status.Identity,
                status.Mode,
                status.UserId,
                status.GroupId);
            handle = null;
            return result;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal static FileStream OpenChildFileReadNoFollow(
        IncusPinnedDirectory directory,
        string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        name = ValidateEntryName(name);
        var descriptor = OpenAt(
            directory.Descriptor,
            name,
            OpenReadOnly | OpenNonBlock | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException("Unable to open a pinned snapshot file without following links.", new Win32Exception(error));
        }
        SafeFileHandle? handle = new((nint)descriptor, ownsHandle: true);
        try
        {
            EnsureRegularFile(handle);
            var result = new FileStream(handle, FileAccess.Read, bufferSize: 64 * 1024, isAsync: false);
            handle = null;
            return result;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal static FileStream CreatePrivateChildFileNoFollow(
        IncusPinnedDirectory directory,
        string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        name = ValidateEntryName(name);
        const uint ownerReadWriteMode = 0x180; // 0600
        var descriptor = OpenAtWithMode(
            directory.Descriptor,
            name,
            OpenWriteOnly | OpenCreate | OpenExclusive | OpenNoFollow | OpenCloseOnExec,
            ownerReadWriteMode);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException("Unable to create a private pinned-directory file.", new Win32Exception(error));
        }
        SafeFileHandle? handle = new((nint)descriptor, ownsHandle: true);
        try
        {
            var status = ReadStatus(handle, RegularFileType, "private pinned-directory file");
            var identity = IncusHostIdentity.GetEffectiveIdentity();
            if (status.UserId != identity.UserId || status.GroupId != identity.GroupId)
                throw new InvalidOperationException("A private pinned-directory file has unexpected ownership.");
            if (SetFileMode(descriptor, ownerReadWriteMode) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException("Unable to set private pinned-directory file mode.", new Win32Exception(error));
            }
            var result = new FileStream(handle, FileAccess.Write, bufferSize: 64 * 1024, isAsync: false);
            handle = null;
            return result;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal static void ReplaceChildFileAtomically(
        IncusPinnedDirectory directory,
        string temporaryName,
        string finalName)
    {
        ArgumentNullException.ThrowIfNull(directory);
        temporaryName = ValidateEntryName(temporaryName);
        finalName = ValidateEntryName(finalName);
        if (RenameAt(directory.Descriptor, temporaryName, directory.Descriptor, finalName) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException("Unable to publish a private pinned-directory file atomically.", new Win32Exception(error));
        }
        FlushDirectory(directory);
    }

    internal static void FlushDirectory(IncusPinnedDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var descriptor = OpenAt(
            directory.Descriptor,
            ".",
            OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException("Unable to open a pinned directory for durable flush.", new Win32Exception(error));
        }
        using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        if (FlushFile(descriptor) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException("Unable to durably flush a pinned directory.", new Win32Exception(error));
        }
    }

    internal static void DeleteChildFileNoFollow(
        IncusPinnedDirectory directory,
        string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        name = ValidateEntryName(name);
        if (UnlinkAt(directory.Descriptor, name, 0) == 0)
            return;
        var error = Marshal.GetLastPInvokeError();
        if (error == 2) // ENOENT
            return;
        throw new IOException("Unable to delete a private pinned-directory file.", new Win32Exception(error));
    }

    internal static string ReadChildSymbolicLinkNoFollow(
        IncusPinnedDirectory directory,
        string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        name = ValidateEntryName(name);
        var entry = new FileInfo(Path.Combine(directory.DescriptorPath, name));
        entry.Refresh();
        return entry.LinkTarget
            ?? throw new IOException("A pinned snapshot symbolic link changed type while it was read.");
    }

    internal static UnixFileMode GetRegularFileMode(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return ReadStatus(stream.SafeFileHandle, RegularFileType, "pinned snapshot file").Mode;
    }

    internal static IncusFileStatus GetRegularFileStatus(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return ReadStatus(stream.SafeFileHandle, RegularFileType, "pinned regular file");
    }

    private static SafeFileHandle OpenDirectoryHandle(string path)
    {
        var descriptor = Open(path, OpenPath | OpenDirectory | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                "Unable to pin a host mount directory without following links.",
                new Win32Exception(error));
        }
        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static void EnsureRegularFile(SafeFileHandle handle)
    {
        _ = ReadStatus(handle, RegularFileType, "isolation-snapshot source");
    }

    private static IncusFileStatus ReadStatus(
        SafeFileHandle handle,
        ushort expectedFileType,
        string description)
    {
        var descriptor = handle.DangerousGetHandle().ToInt32();
        if (Statx(descriptor, string.Empty, AtEmptyPath, StatxBasicStats, out var status) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"Unable to inspect {description} inode identity.", new Win32Exception(error));
        }
        if ((status.Mode & FileTypeMask) != expectedFileType)
            throw new IOException($"The {description} has an unsupported inode type.");
        return new IncusFileStatus(
            new IncusFileIdentity(status.DeviceMajor, status.DeviceMinor, status.Inode),
            ToUnixFileMode(status.Mode),
            status.UserId,
            status.GroupId);
    }

    private static UnixFileMode ToUnixFileMode(ushort mode) =>
        (UnixFileMode)(mode & 0x0FFF);

    private static string ValidateEntryName(string? name)
    {
        if (string.IsNullOrEmpty(name)
            || name is "." or ".."
            || name.Contains('/')
            || name.Contains('\0'))
        {
            throw new IOException("A pinned snapshot directory returned an invalid child name.");
        }
        return name;
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenWithMode(string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(int directoryDescriptor, string path, int flags);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAtWithMode(int directoryDescriptor, string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "renameat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt(
        int oldDirectoryDescriptor,
        string oldPath,
        int newDirectoryDescriptor,
        string newPath);

    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnlinkAt(int directoryDescriptor, string path, int flags);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(
        int directoryDescriptor,
        string path,
        int flags,
        uint mask,
        out StatxBuffer status);

    [LibraryImport("libc", EntryPoint = "mkdir", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CreateDirectory(string path, uint mode);

    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static partial int SetFileMode(int descriptor, uint mode);

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int AcquireFileLock(int descriptor, int operation);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int FlushFile(int descriptor);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(20)]
        internal uint UserId;

        [FieldOffset(24)]
        internal uint GroupId;

        [FieldOffset(28)]
        internal ushort Mode;

        [FieldOffset(32)]
        internal ulong Inode;

        [FieldOffset(136)]
        internal uint DeviceMajor;

        [FieldOffset(140)]
        internal uint DeviceMinor;
    }
}

internal readonly record struct IncusFileIdentity(uint DeviceMajor, uint DeviceMinor, ulong Inode);

internal readonly record struct IncusFileStatus(
    IncusFileIdentity Identity,
    UnixFileMode Mode,
    uint UserId,
    uint GroupId);

internal enum IncusDirectoryEntryKind
{
    Directory,
    RegularFile,
    SymbolicLink,
    Unsupported,
}

internal readonly record struct IncusDirectoryEntryMetadata(
    IncusDirectoryEntryKind Kind,
    UnixFileMode Mode);

internal sealed class IncusPinnedDirectory : IDisposable
{
    private SafeFileHandle? _handle;

    internal IncusPinnedDirectory(
        SafeFileHandle handle,
        IncusFileIdentity identity,
        UnixFileMode mode,
        uint userId,
        uint groupId)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        Identity = identity;
        Mode = mode;
        UserId = userId;
        GroupId = groupId;
    }

    internal IncusFileIdentity Identity { get; }
    internal UnixFileMode Mode { get; }
    internal uint UserId { get; }
    internal uint GroupId { get; }
    internal int Descriptor => (_handle
        ?? throw new ObjectDisposedException(nameof(IncusPinnedDirectory)))
        .DangerousGetHandle()
        .ToInt32();
    internal string DescriptorPath => $"/proc/self/fd/{Descriptor}";

    public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
}
