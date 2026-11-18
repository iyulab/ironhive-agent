using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace IronHive.Agent.Tools;

/// <summary>
/// The directory view a glob walks: the same as <see cref="DirectoryInfoWrapper"/> except that a link to a directory (a symbolic
/// link or a junction) below the starting folder is not entered. A link back up the tree would make the walk endless, and a
/// link out of the tree is not part of the folder the model asked about. The links left out are collected so the tool can say so.
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

        foreach (var entry in _directory.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            if (entry is DirectoryInfo directory)
            {
                if (IsDirectoryLink(directory))
                {
                    _skippedLinks.Add(directory.FullName);
                    continue;
                }

                yield return new LinkSkippingDirectoryInfo(directory, _skippedLinks);
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

        if (IsDirectoryLink(matches[0]))
        {
            _skippedLinks.Add(matches[0].FullName);
            return null;
        }

        return new LinkSkippingDirectoryInfo(matches[0], _skippedLinks);
    }

    public override FileInfoBase? GetFile(string path) =>
        new FileInfoWrapper(new FileInfo(Path.Combine(_directory.FullName, path)));
}
