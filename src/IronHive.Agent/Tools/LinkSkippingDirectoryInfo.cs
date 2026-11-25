using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace IronHive.Agent.Tools;

/// <summary>
/// The directory view a glob walks: the same as <see cref="DirectoryInfoWrapper"/> except that a link to a directory (a symbolic
/// link or a junction) below the starting folder is not entered. A link back up the tree would make the walk endless, and a
/// link out of the tree is not part of the folder the model asked about. A link the pattern would have gone into is collected so
/// the tool can say so; one the pattern never reaches is not. A folder that vanishes while the walk runs reads as empty, as it
/// does for <see cref="DirectoryInfoWrapper"/>.
/// </summary>
internal sealed class LinkSkippingDirectoryInfo : DirectoryInfoBase
{
    private readonly DirectoryInfo _directory;
    private readonly List<string> _skippedLinks;
    private readonly string? _name;

    public LinkSkippingDirectoryInfo(DirectoryInfo directory, List<string> skippedLinks)
        : this(directory, skippedLinks, null)
    {
    }

    private LinkSkippingDirectoryInfo(DirectoryInfo directory, List<string> skippedLinks, string? name)
    {
        _directory = directory;
        _skippedLinks = skippedLinks;
        _name = name;
    }

    public override string Name => _name ?? _directory.Name;

    public override string FullName => _directory.FullName;

    public override DirectoryInfoBase? ParentDirectory =>
        _directory.Parent is { } parent ? new LinkSkippingDirectoryInfo(parent, _skippedLinks) : null;

    /// <summary>
    /// Whether the directory is a link to another directory (a symbolic link or a junction). A cloud placeholder folder carries a
    /// reparse point and has no link target: it is an ordinary directory. The attribute is read first because it is already on the
    /// enumerated entry, while the target costs a call to the file system.
    /// </summary>
    internal static bool IsDirectoryLink(DirectoryInfo directory) =>
        directory.Attributes.HasFlag(FileAttributes.ReparsePoint) && directory.LinkTarget is not null;

    public override IEnumerable<FileSystemInfoBase> EnumerateFileSystemInfos()
    {
        if (!_directory.Exists)
        {
            yield break;
        }

        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = _directory.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly);
        }
        catch (DirectoryNotFoundException)
        {
            // Removed since its parent was read (Exists above answers from what was read then)
            yield break;
        }

        foreach (var entry in entries)
        {
            if (entry is DirectoryInfo directory)
            {
                yield return Wrap(directory);
            }
            else if (entry is FileInfo file)
            {
                yield return new FileInfoWrapper(file);
            }
        }
    }

    public override DirectoryInfoBase? GetDirectory(string path)
    {
        if (string.Equals(path, "..", StringComparison.Ordinal))
        {
            return new LinkSkippingDirectoryInfo(new DirectoryInfo(Path.Combine(_directory.FullName, path)), _skippedLinks, "..");
        }

        var matches = _directory.GetDirectories(path);
        if (matches.Length == 0)
        {
            return null;
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException($"More than one sub directory is found under {_directory.FullName} with name {path}.");
        }

        return Wrap(matches[0]);
    }

    public override FileInfoBase? GetFile(string path) =>
        new FileInfoWrapper(new FileInfo(Path.Combine(_directory.FullName, path)));

    private DirectoryInfoBase Wrap(DirectoryInfo directory) =>
        IsDirectoryLink(directory)
            ? new NotEnteredLink(directory, this, _skippedLinks)
            : new LinkSkippingDirectoryInfo(directory, _skippedLinks);

    // A link as the walk sees it: a directory with nothing in it. The matcher asks a directory for its entries only when the
    // pattern goes into it, so only then is the link recorded as left out.
    private sealed class NotEnteredLink(DirectoryInfo link, DirectoryInfoBase parent, List<string> skippedLinks) : DirectoryInfoBase
    {
        public override string Name => link.Name;

        public override string FullName => link.FullName;

        public override DirectoryInfoBase? ParentDirectory => parent;

        public override IEnumerable<FileSystemInfoBase> EnumerateFileSystemInfos()
        {
            skippedLinks.Add(link.FullName);
            return [];
        }

        public override DirectoryInfoBase? GetDirectory(string path) => null;

        public override FileInfoBase? GetFile(string path) => null;
    }
}
