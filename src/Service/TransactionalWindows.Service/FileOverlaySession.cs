using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TransactionalWindows.Core.Domain;

namespace TransactionalWindows.Service;

/// <summary>
/// A user-mode overlay for one local directory. It demonstrates the transaction
/// semantics used by the future minifilter: baseline files are never written
/// during a session and the first write performs copy-on-write.
/// </summary>
public sealed class FileOverlaySession
{
    private readonly object _gate = new();
    private readonly string _baselineRoot;
    private readonly string _overlayRoot;
    private readonly string _contentRoot;
    private readonly string _tombstoneRoot;
    private readonly string _journalPath;
    private readonly TransactionId _transactionId;
    private readonly Dictionary<string, FileChange> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tombstones = new(StringComparer.OrdinalIgnoreCase);

    public FileOverlaySession(TransactionId transactionId, string baselineRoot, string overlayRoot)
    {
        _transactionId = transactionId;
        _baselineRoot = Path.GetFullPath(baselineRoot);
        _overlayRoot = Path.GetFullPath(overlayRoot);
        var baselinePrefix = _baselineRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (string.Equals(_baselineRoot, _overlayRoot, StringComparison.OrdinalIgnoreCase) ||
            _overlayRoot.StartsWith(baselinePrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Overlay root must be outside the baseline root.", nameof(overlayRoot));
        _contentRoot = Path.Combine(_overlayRoot, "content");
        _tombstoneRoot = Path.Combine(_overlayRoot, "tombstones");
        _journalPath = Path.Combine(_overlayRoot, "file-changes.jsonl");
        Directory.CreateDirectory(_baselineRoot);
        Directory.CreateDirectory(_contentRoot);
        Directory.CreateDirectory(_tombstoneRoot);
    }

    public string BaselineRoot => _baselineRoot;
    public string OverlayRoot => _overlayRoot;
    public TransactionId TransactionId => _transactionId;
    public IReadOnlyList<FileChange> Changes
    {
        get { lock (_gate) return _changes.Values.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    public byte[] ReadAllBytes(string relativePath)
    {
        var relative = Normalize(relativePath);
        lock (_gate)
        {
            if (_tombstones.Contains(relative)) throw new FileNotFoundException("The file is deleted in this transaction.", relative);
            var overlay = OverlayPath(relative);
            if (File.Exists(overlay)) return File.ReadAllBytes(overlay);
        }

        var baseline = BaselinePath(relative);
        if (!File.Exists(baseline)) throw new FileNotFoundException("The file does not exist in baseline or overlay.", relative);
        return File.ReadAllBytes(baseline);
    }

    public void WriteAllBytes(string relativePath, byte[] content, ProcessNodeId? sourceProcessNodeId = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var relative = Normalize(relativePath);
        lock (_gate)
        {
            var operation = File.Exists(BaselinePath(relative)) || File.Exists(OverlayPath(relative))
                ? FileChangeOperation.Modify
                : FileChangeOperation.Create;
            EnsureCopyOnWrite(relative);
            var target = OverlayPath(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
            _tombstones.Remove(relative);
            Persist(ChangeFor(relative, operation, sourceProcessNodeId));
        }
    }

    public void CreateFile(string relativePath, byte[]? content = null, ProcessNodeId? sourceProcessNodeId = null)
    {
        var relative = Normalize(relativePath);
        lock (_gate)
        {
            if (ExistsEffective(relative)) throw new IOException("Path already exists: " + relative);
            var target = OverlayPath(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content ?? Array.Empty<byte>());
            _tombstones.Remove(relative);
            var change = ChangeFor(relative, FileChangeOperation.Create, sourceProcessNodeId);
            if (_changes.TryGetValue(relative, out var existing) && !existing.BaselineExists)
                change = change with { Operation = FileChangeOperation.Create };
            Persist(change);
        }
    }

    public void DeleteFile(string relativePath, ProcessNodeId? sourceProcessNodeId = null)
    {
        var relative = Normalize(relativePath);
        lock (_gate)
        {
            if (!ExistsEffective(relative)) throw new FileNotFoundException("The file does not exist.", relative);
            var overlay = OverlayPath(relative);
            if (File.Exists(overlay)) File.Delete(overlay);
            _tombstones.Add(relative);
            var tombstone = TombstonePath(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(tombstone)!);
            File.WriteAllText(tombstone, string.Empty, Encoding.UTF8);
            if (!File.Exists(BaselinePath(relative)))
            {
                _changes.Remove(relative);
                AppendJournal(new FileChange
                {
                    Id = FileChangeId.New(), TransactionId = _transactionId, Path = relative, ObjectType = FileObjectType.File,
                    Operation = FileChangeOperation.Delete, BaselineExists = false, OverlayExists = false,
                    SourceProcessNodeId = sourceProcessNodeId
                });
            }
            else
            {
                Persist(ChangeFor(relative, FileChangeOperation.Delete, sourceProcessNodeId));
            }
        }
    }

    public void RenameFile(string oldRelativePath, string newRelativePath, ProcessNodeId? sourceProcessNodeId = null)
    {
        var oldRelative = Normalize(oldRelativePath);
        var newRelative = Normalize(newRelativePath);
        lock (_gate)
        {
            if (!ExistsEffective(oldRelative)) throw new FileNotFoundException("The source file does not exist.", oldRelative);
            if (ExistsEffective(newRelative)) throw new IOException("Path already exists: " + newRelative);
            EnsureCopyOnWrite(oldRelative);
            var oldOverlay = OverlayPath(oldRelative);
            var newOverlay = OverlayPath(newRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(newOverlay)!);
            File.Move(oldOverlay, newOverlay);
            var sourceWasCreated = _changes.TryGetValue(oldRelative, out var priorSource) && !priorSource.BaselineExists;
            if (sourceWasCreated)
            {
                _changes.Remove(oldRelative);
                _tombstones.Remove(oldRelative);
            }
            else
            {
                _tombstones.Add(oldRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(TombstonePath(oldRelative))!);
                File.WriteAllText(TombstonePath(oldRelative), string.Empty, Encoding.UTF8);
            }
            var baseline = BaselinePath(oldRelative);
            var overlay = new FileInfo(newOverlay);
            var change = new FileChange
            {
                Id = FileChangeId.New(), TransactionId = _transactionId, Path = newRelative, OldPath = oldRelative,
                ObjectType = FileObjectType.File, Operation = FileChangeOperation.Rename,
                BaselineExists = File.Exists(baseline), OverlayExists = true,
                BaselineDigest = File.Exists(baseline) ? Digest(baseline) : null, OverlayDigest = Digest(newOverlay),
                BaselineLength = File.Exists(baseline) ? new FileInfo(baseline).Length : null, OverlayLength = overlay.Length,
                BaselineLastWriteTimeUtc = File.Exists(baseline) ? File.GetLastWriteTimeUtc(baseline) : null,
                OverlayLastWriteTimeUtc = overlay.LastWriteTimeUtc, SourceProcessNodeId = sourceProcessNodeId
            };
            if (sourceWasCreated)
                change = change with { Operation = FileChangeOperation.Create, BaselineExists = false, BaselineDigest = null, BaselineLength = null, BaselineLastWriteTimeUtc = null };
            Persist(change);
        }
    }

    public bool ExistsEffective(string relativePath)
    {
        var relative = Normalize(relativePath);
        lock (_gate)
        {
            return !_tombstones.Contains(relative) && (File.Exists(OverlayPath(relative)) || File.Exists(BaselinePath(relative)));
        }
    }

    public string GetOverlayPath(string relativePath) => OverlayPath(Normalize(relativePath));

    public void Discard()
    {
        lock (_gate)
        {
            if (Directory.Exists(_overlayRoot)) Directory.Delete(_overlayRoot, recursive: true);
            _changes.Clear();
            _tombstones.Clear();
        }
    }

    private FileChange ChangeFor(string relative, FileChangeOperation operation, ProcessNodeId? sourceProcessNodeId)
    {
        var baseline = BaselinePath(relative);
        var overlay = OverlayPath(relative);
        var baselineExists = File.Exists(baseline);
        var overlayExists = File.Exists(overlay);
        var overlayInfo = overlayExists ? new FileInfo(overlay) : null;
        return new FileChange
        {
            Id = FileChangeId.New(), TransactionId = _transactionId, Path = relative, ObjectType = FileObjectType.File, Operation = operation,
            BaselineExists = baselineExists, OverlayExists = overlayExists, BaselineDigest = baselineExists ? Digest(baseline) : null,
            OverlayDigest = overlayExists ? Digest(overlay) : null, ContentReference = overlayExists ? overlay : null,
            BaselineLength = baselineExists ? new FileInfo(baseline).Length : null, OverlayLength = overlayInfo?.Length,
            BaselineLastWriteTimeUtc = baselineExists ? File.GetLastWriteTimeUtc(baseline) : null,
            OverlayLastWriteTimeUtc = overlayInfo?.LastWriteTimeUtc, SourceProcessNodeId = sourceProcessNodeId
        };
    }

    private void EnsureCopyOnWrite(string relative)
    {
        var overlay = OverlayPath(relative);
        if (File.Exists(overlay)) return;
        var baseline = BaselinePath(relative);
        if (File.Exists(baseline))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(overlay)!);
            File.Copy(baseline, overlay, overwrite: false);
        }
    }

    private void Persist(FileChange change)
    {
        _changes[change.Path] = change;
        AppendJournal(change);
    }

    private void AppendJournal(FileChange change)
    {
        var json = JsonSerializer.Serialize(change);
        File.AppendAllText(_journalPath, json + Environment.NewLine, Encoding.UTF8);
    }

    private string Normalize(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) throw new ArgumentException("Only non-empty relative paths are supported.", nameof(relativePath));
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var combined = Path.GetFullPath(Path.Combine(_baselineRoot, normalized));
        var prefix = _baselineRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Path escapes the transaction root.", nameof(relativePath));
        return Path.GetRelativePath(_baselineRoot, combined);
    }

    private string BaselinePath(string relative) => Path.Combine(_baselineRoot, relative);
    private string OverlayPath(string relative) => Path.Combine(_contentRoot, relative);
    private string TombstonePath(string relative) => Path.Combine(_tombstoneRoot, relative + ".deleted");

    private static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
