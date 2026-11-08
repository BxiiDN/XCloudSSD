using XCloudSSD.Core;

namespace XCloudSSD.Metadata;

/// <summary>One file or folder in the in-memory view of a cloud namespace.</summary>
public sealed class MetaNode
{
    private static long _nextIndex = 1;

    public MetaNode(string id, string name, bool isFolder)
    {
        Id = id;
        Name = name;
        IsFolder = isFolder;
        IndexNumber = Interlocked.Increment(ref _nextIndex);
    }

    public string Id { get; internal set; }
    public string Name { get; internal set; }
    public bool IsFolder { get; }
    public long Size { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Modified { get; set; }
    public DateTimeOffset Accessed { get; set; }
    public string? Md5 { get; set; }
    /// <summary>Windows attributes as stored in the cloud (without DIRECTORY/NORMAL normalization).</summary>
    public uint StoredAttributes { get; set; }
    public long IndexNumber { get; }
    public MetaNode? Parent { get; internal set; }

    /// <summary>
    /// Number of open write sessions holding uncommitted local changes. While non-zero, remote
    /// change notifications do not overwrite this node's size/content metadata.
    /// </summary>
    public int PendingWriters;

    /// <summary>True for a file created locally whose content has not been committed to the cloud yet.</summary>
    public bool IsLocalOnly => Id.StartsWith(LocalIdPrefix, StringComparison.Ordinal);

    public const string LocalIdPrefix = "local:";

    internal Dictionary<string, MetaNode>? Children;
    internal DateTime ChildrenLoadedUtc;
    internal Task? LoadTask;

    public string FullPath
    {
        get
        {
            if (Parent is null) return "\\";
            var parts = new Stack<string>();
            for (var n = this; n.Parent is not null; n = n.Parent) parts.Push(n.Name);
            return "\\" + string.Join('\\', parts);
        }
    }

    public void ApplyItem(CloudItem item)
    {
        Id = item.Id;
        if (!IsFolder && PendingWriters == 0)
        {
            Size = item.Size;
            Md5 = item.Md5;
        }
        Created = item.Created;
        Modified = item.Modified;
        StoredAttributes = item.WindowsAttributes ?? 0;
    }
}

/// <summary>
/// Path → cloud-object map for one account. Folders are listed lazily from the cloud on first
/// access and refreshed when stale or when the provider's change feed reports a change.
/// Only metadata is held here; no file content.
/// </summary>
public sealed class MetadataTree
{
    private readonly ICloudProvider _provider;
    private readonly object _sync = new();
    private readonly Dictionary<string, MetaNode> _byId = new(StringComparer.Ordinal);

    public MetadataTree(ICloudProvider provider, TimeSpan childrenTtl)
    {
        _provider = provider;
        ChildrenTtl = childrenTtl;
        Root = new MetaNode(provider.RootId, "", isFolder: true)
        {
            Created = DateTimeOffset.UtcNow,
            Modified = DateTimeOffset.UtcNow,
        };
        _byId[Root.Id] = Root;
    }

    public MetaNode Root { get; }
    public TimeSpan ChildrenTtl { get; set; }

    public static StringComparer NameComparer => StringComparer.OrdinalIgnoreCase;

    public static string[] SplitPath(string path)
        => path.Split('\\', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Resolve a Windows path ("\a\b.txt") to a node, or null when it does not exist.</summary>
    public async Task<MetaNode?> ResolveAsync(string path, CancellationToken ct)
    {
        var node = Root;
        foreach (var part in SplitPath(path))
        {
            if (!node.IsFolder) return null;
            await EnsureChildrenAsync(node, ct).ConfigureAwait(false);
            lock (_sync)
            {
                if (node.Children is null || !node.Children.TryGetValue(part, out var child)) return null;
                node = child;
            }
        }
        return node;
    }

    public async Task<IReadOnlyList<MetaNode>> GetChildrenAsync(MetaNode folder, CancellationToken ct)
    {
        await EnsureChildrenAsync(folder, ct).ConfigureAwait(false);
        lock (_sync)
        {
            return folder.Children is null ? Array.Empty<MetaNode>() : folder.Children.Values.ToList();
        }
    }

    public MetaNode? TryGetChild(MetaNode folder, string name)
    {
        lock (_sync)
        {
            return folder.Children is not null && folder.Children.TryGetValue(name, out var c) ? c : null;
        }
    }

    public MetaNode? FindById(string id)
    {
        lock (_sync) return _byId.TryGetValue(id, out var n) ? n : null;
    }

    private Task EnsureChildrenAsync(MetaNode folder, CancellationToken ct)
    {
        Task? load;
        lock (_sync)
        {
            if (folder.IsLocalOnly)
            {
                folder.Children ??= new Dictionary<string, MetaNode>(NameComparer);
                return Task.CompletedTask;
            }
            var fresh = folder.Children is not null && DateTime.UtcNow - folder.ChildrenLoadedUtc < ChildrenTtl;
            if (fresh) return Task.CompletedTask;
            if (folder.LoadTask is { IsCompleted: false }) return folder.LoadTask;
            // Stale but present: refresh, yet keep serving the previous listing if the refresh fails.
            load = folder.LoadTask = LoadChildrenAsync(folder, ct);
        }
        return folder.Children is not null ? SwallowWhenStale(load) : load;
    }

    private static async Task SwallowWhenStale(Task t)
    {
        try
        {
            await t.ConfigureAwait(false);
        }
        catch (CloudException ex) when (ex.IsRetryable)
        {
            // Offline: keep the last known listing.
        }
    }

    private async Task LoadChildrenAsync(MetaNode folder, CancellationToken ct)
    {
        var items = await _provider.ListChildrenAsync(folder.Id, ct).ConfigureAwait(false);
        lock (_sync)
        {
            var old = folder.Children ?? new Dictionary<string, MetaNode>(NameComparer);
            var next = new Dictionary<string, MetaNode>(NameComparer);
            foreach (var item in items.Where(i => !i.IsVirtualDocument).OrderByDescending(i => i.Modified))
            {
                var name = NameCodec.ToWindows(item.Name);
                if (next.ContainsKey(name)) continue; // duplicate names: newest wins, older ones stay hidden
                if (_byId.TryGetValue(item.Id, out var existing) && existing.IsFolder == item.IsFolder)
                {
                    existing.Name = name;
                    existing.Parent = folder;
                    existing.ApplyItem(item);
                    next[name] = existing;
                }
                else
                {
                    var n = CreateNode(item, name, folder);
                    next[name] = n;
                }
            }
            // Keep local, not-yet-committed children (e.g. a file being written right now).
            var kept = new HashSet<MetaNode>(next.Values, ReferenceEqualityComparer.Instance);
            foreach (var (name, n) in old)
            {
                if (kept.Contains(n)) continue;
                if ((n.IsLocalOnly || n.PendingWriters > 0) && !next.ContainsKey(name)) next[name] = n;
                else ForgetSubtree(n);
            }
            folder.Children = next;
            folder.ChildrenLoadedUtc = DateTime.UtcNow;
        }
    }

    private MetaNode CreateNode(CloudItem item, string windowsName, MetaNode parent)
    {
        var n = new MetaNode(item.Id, windowsName, item.IsFolder) { Parent = parent, Accessed = item.Modified };
        n.ApplyItem(item);
        if (item.IsFolder) n.Size = 0;
        _byId[item.Id] = n;
        return n;
    }

    private void ForgetSubtree(MetaNode n)
    {
        if (n.PendingWriters > 0) return;
        if (_byId.TryGetValue(n.Id, out var cur) && ReferenceEquals(cur, n)) _byId.Remove(n.Id);
        if (n.Children is null) return;
        foreach (var c in n.Children.Values) ForgetSubtree(c);
    }

    /// <summary>Insert an item that was just created in the cloud (or locally pending).</summary>
    public MetaNode AddChild(MetaNode parent, CloudItem item, string windowsName)
    {
        lock (_sync)
        {
            parent.Children ??= new Dictionary<string, MetaNode>(NameComparer);
            var n = CreateNode(item, windowsName, parent);
            parent.Children[windowsName] = n;
            return n;
        }
    }

    /// <summary>Create a placeholder for a new file whose content is still being written.</summary>
    public MetaNode AddLocalFile(MetaNode parent, string windowsName, uint attributes)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new CloudItem
        {
            Id = MetaNode.LocalIdPrefix + Guid.NewGuid().ToString("N"),
            Name = windowsName,
            IsFolder = false,
            Size = 0,
            Created = now,
            Modified = now,
            WindowsAttributes = attributes,
        };
        return AddChild(parent, item, windowsName);
    }

    public void Rekey(MetaNode node, string newId)
    {
        lock (_sync)
        {
            if (_byId.TryGetValue(node.Id, out var cur) && ReferenceEquals(cur, node)) _byId.Remove(node.Id);
            node.Id = newId;
            _byId[newId] = node;
        }
    }

    public void Remove(MetaNode node)
    {
        lock (_sync)
        {
            if (node.Parent?.Children is { } siblings && siblings.TryGetValue(node.Name, out var cur) && ReferenceEquals(cur, node))
                siblings.Remove(node.Name);
            if (_byId.TryGetValue(node.Id, out var byId) && ReferenceEquals(byId, node)) _byId.Remove(node.Id);
        }
    }

    public void Move(MetaNode node, MetaNode newParent, string newName)
    {
        lock (_sync)
        {
            if (node.Parent?.Children is { } oldSiblings && oldSiblings.TryGetValue(node.Name, out var cur) && ReferenceEquals(cur, node))
                oldSiblings.Remove(node.Name);
            newParent.Children ??= new Dictionary<string, MetaNode>(NameComparer);
            if (newParent.Children.TryGetValue(newName, out var replaced) && !ReferenceEquals(replaced, node))
                ForgetSubtree(replaced);
            node.Name = newName;
            node.Parent = newParent;
            newParent.Children[newName] = node;
        }
    }

    public void InvalidateFolder(MetaNode folder)
    {
        lock (_sync) folder.ChildrenLoadedUtc = DateTime.MinValue;
    }

    public void InvalidateAll()
    {
        lock (_sync)
            foreach (var n in _byId.Values)
                if (n.IsFolder) n.ChildrenLoadedUtc = DateTime.MinValue;
    }

    /// <summary>Apply the provider change feed so other devices' edits show up without a full rescan.</summary>
    public void ApplyChanges(ChangeSet changes)
    {
        lock (_sync)
        {
            foreach (var ch in changes.Changes)
            {
                _byId.TryGetValue(ch.Id, out var node);
                if (ch.Removed || ch.Item is null)
                {
                    if (node is not null && node.PendingWriters == 0 && node.Parent is not null)
                    {
                        if (node.Parent.Children is { } s && s.TryGetValue(node.Name, out var cur) && ReferenceEquals(cur, node))
                            s.Remove(node.Name);
                        ForgetSubtree(node);
                    }
                    continue;
                }

                var item = ch.Item;
                if (item.IsVirtualDocument) continue;
                var parentId = item.Parents.FirstOrDefault();
                _byId.TryGetValue(parentId ?? "", out var parent);
                if (parentId == _provider.RootId) parent = Root;

                if (node is not null)
                {
                    if (node.PendingWriters > 0) continue;
                    var newName = NameCodec.ToWindows(item.Name);
                    node.ApplyItem(item);
                    if (parent is null)
                    {
                        // Moved outside the part of the tree we have loaded.
                        if (node.Parent?.Children is { } s && s.TryGetValue(node.Name, out var cur) && ReferenceEquals(cur, node))
                            s.Remove(node.Name);
                        ForgetSubtree(node);
                    }
                    else if (!ReferenceEquals(parent, node.Parent) || !NameComparer.Equals(newName, node.Name))
                    {
                        if (node.Parent?.Children is { } s && s.TryGetValue(node.Name, out var cur) && ReferenceEquals(cur, node))
                            s.Remove(node.Name);
                        node.Name = newName;
                        node.Parent = parent;
                        if (parent.Children is not null) parent.Children[newName] = node;
                    }
                }
                else if (parent?.Children is not null)
                {
                    var name = NameCodec.ToWindows(item.Name);
                    if (!parent.Children.ContainsKey(name))
                        parent.Children[name] = CreateNode(item, name, parent);
                }
            }
        }
    }

    /// <summary>Build a display path for a search hit by walking known parents (best effort).</summary>
    public string? TryGetPath(string id)
    {
        lock (_sync) return _byId.TryGetValue(id, out var n) ? n.FullPath : null;
    }
}
