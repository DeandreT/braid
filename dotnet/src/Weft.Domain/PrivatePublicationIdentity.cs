namespace Weft.Domain;

/// <summary>The caller-known exact key used to recover a lost publication-reservation acknowledgement.</summary>
public sealed record PrivatePublicationKey
{
    public PrivatePublicationKey(Guid rootId, PrivateObjectId @object, Guid tempId)
    {
        ArgumentNullException.ThrowIfNull(@object);
        RootId = CleanupDomainValue.Id(rootId, nameof(rootId));
        TempId = CleanupDomainValue.Id(tempId, nameof(tempId));
        Object = @object;
    }

    public Guid RootId { get; }
    public PrivateObjectId Object { get; }
    public Guid TempId { get; }
    public RevisionScope Scope => Object.Scope;
}

/// <summary>Actual descriptor-observed Linux identity. It conveys no ownership by itself.</summary>
public sealed record PrivatePublicationInodeIdentity
{
    public PrivatePublicationInodeIdentity(ulong inode, uint deviceMajor, uint deviceMinor, ulong mountId)
    {
        if (inode == 0)
            throw new ArgumentOutOfRangeException(nameof(inode), "An observed inode must be nonzero.");
        if (mountId == 0)
            throw new ArgumentOutOfRangeException(nameof(mountId), "An observed mount identity must be nonzero.");
        Inode = inode;
        DeviceMajor = deviceMajor;
        DeviceMinor = deviceMinor;
        MountId = mountId;
    }

    public ulong Inode { get; }
    public uint DeviceMajor { get; }
    public uint DeviceMinor { get; }
    public ulong MountId { get; }
}

/// <summary>
/// Actual private v2 storage facts. Destructive recovery must preserve the exact
/// boot, namespace and root/control incarnation; mount IDs are not reboot-stable.
/// </summary>
public sealed record PrivatePublicationFileIdentity
{
    public const string Format = "weft.private-publication.linux.v2";

    public PrivatePublicationFileIdentity(Guid rootId, Guid controlNamespaceId, Guid linuxBootId,
        ulong mountNamespaceDevice, ulong mountNamespaceInode, uint effectiveUserId,
        PrivatePublicationInodeIdentity fileIdentity, PrivatePublicationInodeIdentity directoryIdentity)
    {
        ArgumentNullException.ThrowIfNull(fileIdentity);
        ArgumentNullException.ThrowIfNull(directoryIdentity);
        RootId = CleanupDomainValue.Id(rootId, nameof(rootId));
        ControlNamespaceId = CleanupDomainValue.Id(controlNamespaceId, nameof(controlNamespaceId));
        LinuxBootId = CleanupDomainValue.Id(linuxBootId, nameof(linuxBootId));
        if (mountNamespaceInode == 0)
            throw new ArgumentOutOfRangeException(nameof(mountNamespaceInode), "An observed mount namespace inode must be nonzero.");
        if (fileIdentity.DeviceMajor != directoryIdentity.DeviceMajor ||
            fileIdentity.DeviceMinor != directoryIdentity.DeviceMinor ||
            fileIdentity.MountId != directoryIdentity.MountId || fileIdentity.Inode == directoryIdentity.Inode)
            throw new ArgumentException("A temporary file must have its own identity on its containing directory's mount.", nameof(fileIdentity));

        MountNamespaceDevice = mountNamespaceDevice;
        MountNamespaceInode = mountNamespaceInode;
        EffectiveUserId = effectiveUserId;
        FileIdentity = fileIdentity;
        DirectoryIdentity = directoryIdentity;
    }

    public Guid RootId { get; }
    public Guid ControlNamespaceId { get; }
    public Guid LinuxBootId { get; }
    public ulong MountNamespaceDevice { get; }
    public ulong MountNamespaceInode { get; }
    public uint EffectiveUserId { get; }
    public PrivatePublicationInodeIdentity FileIdentity { get; }
    public PrivatePublicationInodeIdentity DirectoryIdentity { get; }
}
