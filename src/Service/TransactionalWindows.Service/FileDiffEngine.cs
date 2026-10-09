using System.Security.Cryptography;
using TransactionalWindows.Core.Domain;

namespace TransactionalWindows.Service;

public static class FileDiffEngine
{
    public static Diff Build(FileOverlaySession session, long generation)
    {
        var changes = session.Changes
            .Where(change => change.Disposition == OperationDisposition.Supported)
            .Where(change => change.BaselineExists || change.OverlayExists || change.Operation == FileChangeOperation.Delete && File.Exists(Path.Combine(session.BaselineRoot, change.Path)))
            .OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(change => change.Operation)
            .ToArray();

        var items = changes.Select(change => new DiffItem
        {
            Id = DiffItemId.New(), Type = change.ObjectType == FileObjectType.Directory ? DiffItemType.Directory : DiffItemType.File,
            FileChangeId = change.Id, Path = change.Path, OldPath = change.OldPath, FileOperation = change.Operation,
            BaselineDigest = change.BaselineDigest, OverlayDigest = change.OverlayDigest,
            SourceProcessNodeId = change.SourceProcessNodeId, Status = DiffItemStatus.Applicable,
            Selection = SelectionState.NotSelected, ApplyState = ApplyState.NotApplied
        }).ToArray();

        var dependencies = BuildDependencies(items);
        var diff = new Diff { Id = DiffId.New(), TransactionId = session.TransactionId, Generation = generation, Items = items, Dependencies = dependencies };
        diff.ValidateDependencies();
        return diff;
    }

    private static IReadOnlyList<Dependency> BuildDependencies(IReadOnlyList<DiffItem> items)
    {
        var dependencies = new List<Dependency>();
        foreach (var item in items.Where(x => x.FileOperation == FileChangeOperation.Rename && x.OldPath is not null))
        {
            var prerequisite = items.FirstOrDefault(x => string.Equals(x.Path, item.OldPath, StringComparison.OrdinalIgnoreCase));
            if (prerequisite is not null && prerequisite.Id != item.Id)
                dependencies.Add(new Dependency(item.Id, prerequisite.Id, "rename source must be resolved first"));
        }
        return dependencies;
    }
}

public sealed record FileCommitResult(bool Succeeded, IReadOnlyList<DiffItemId> Applied, IReadOnlyList<DiffItemId> Skipped, string? Error);

/// <summary>Offline commit adapter for the user-mode overlay proof of concept.
/// The minifilter is intentionally not part of this class.</summary>
public sealed class FileCommitEngine
{
    public FileCommitResult Commit(FileOverlaySession session, Diff diff, IReadOnlySet<DiffItemId> selected)
    {
        diff.ValidateDependencies();
        var byId = diff.Items.ToDictionary(x => x.Id);
        var closure = ExpandDependencies(diff, selected);
        var ordered = TopologicalOrder(diff, closure);
        var applied = new List<DiffItemId>();
        var skipped = diff.Items.Where(x => !closure.Contains(x.Id)).Select(x => x.Id).ToList();
        var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var createdTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var backupRoot = Path.Combine(session.OverlayRoot, "commit-backup");
        Directory.CreateDirectory(backupRoot);

        try
        {
            foreach (var item in ordered)
            {
                if (!byId.TryGetValue(item, out var diffItem) || diffItem.FileChangeId is null) continue;
                var change = session.Changes.First(x => x.Id == diffItem.FileChangeId.Value);
                ValidateBaseline(session, change);
                Apply(session, change, backupRoot, backups, createdTargets);
                applied.Add(item);
            }
            return new FileCommitResult(true, applied, skipped, null);
        }
        catch (Exception ex)
        {
            Restore(backups, createdTargets);
            return new FileCommitResult(false, applied, skipped, ex.Message);
        }
        finally
        {
            if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, recursive: true);
        }
    }

    private static HashSet<DiffItemId> ExpandDependencies(Diff diff, IReadOnlySet<DiffItemId> selected)
    {
        var result = new HashSet<DiffItemId>(selected);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var dependency in diff.Dependencies.Where(x => result.Contains(x.Dependent)))
                changed |= result.Add(dependency.Prerequisite);
        }
        return result;
    }

    private static IReadOnlyList<DiffItemId> TopologicalOrder(Diff diff, IReadOnlySet<DiffItemId> selected)
    {
        var pending = selected.ToHashSet();
        var result = new List<DiffItemId>();
        while (pending.Count > 0)
        {
            var next = pending.FirstOrDefault(id => diff.Dependencies.All(d => d.Dependent != id || !pending.Contains(d.Prerequisite)));
            if (next == default) throw new InvalidOperationException("Diff dependency graph contains a cycle.");
            result.Add(next);
            pending.Remove(next);
        }
        return result;
    }

    private static void ValidateBaseline(FileOverlaySession session, FileChange change)
    {
        var oldPath = change.OldPath ?? change.Path;
        var baselinePath = Path.Combine(session.BaselineRoot, oldPath);
        var exists = File.Exists(baselinePath);
        if (change.Operation == FileChangeOperation.Create)
        {
            if (exists) throw new IOException("Commit conflict: create target already exists: " + change.Path);
            return;
        }
        if (change.Operation == FileChangeOperation.Rename && File.Exists(Path.Combine(session.BaselineRoot, change.Path)))
            throw new IOException("Commit conflict: rename target already exists: " + change.Path);
        if (change.BaselineExists != exists) throw new IOException("Commit conflict: baseline existence changed: " + oldPath);
        if (exists && !string.Equals(change.BaselineDigest, Digest(baselinePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Commit conflict: baseline content changed: " + oldPath);
    }

    private static void Apply(FileOverlaySession session, FileChange change, string backupRoot, IDictionary<string, string> backups, ISet<string> createdTargets)
    {
        var target = Path.Combine(session.BaselineRoot, change.Path);
        var source = change.OldPath is null ? target : Path.Combine(session.BaselineRoot, change.OldPath);
        if (change.Operation is FileChangeOperation.Modify or FileChangeOperation.Create)
        {
            var content = change.ContentReference ?? session.GetOverlayPath(change.Path);
            if (!File.Exists(content)) throw new FileNotFoundException("Overlay content is missing.", content);
            BackupIfNeeded(target, backupRoot, backups);
            if (!File.Exists(target)) createdTargets.Add(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + ".tw-" + Guid.NewGuid().ToString("N") + ".tmp";
            File.Copy(content, temp, overwrite: true);
            File.Move(temp, target, overwrite: true);
        }
        else if (change.Operation == FileChangeOperation.Delete)
        {
            BackupIfNeeded(target, backupRoot, backups);
            if (File.Exists(target)) File.Delete(target);
        }
        else if (change.Operation == FileChangeOperation.Rename)
        {
            BackupIfNeeded(source, backupRoot, backups);
            BackupIfNeeded(target, backupRoot, backups);
            if (!File.Exists(target)) createdTargets.Add(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(source)) File.Move(source, target, overwrite: true);
            var content = change.ContentReference ?? session.GetOverlayPath(change.Path);
            if (!File.Exists(content)) throw new FileNotFoundException("Overlay content is missing.", content);
            var temp = target + ".tw-" + Guid.NewGuid().ToString("N") + ".tmp";
            File.Copy(content, temp, overwrite: true);
            File.Move(temp, target, overwrite: true);
        }
    }

    private static void BackupIfNeeded(string path, string backupRoot, IDictionary<string, string> backups)
    {
        if (!File.Exists(path) || backups.ContainsKey(path)) return;
        var backup = Path.Combine(backupRoot, Guid.NewGuid().ToString("N") + ".bak");
        File.Copy(path, backup, overwrite: false);
        backups[path] = backup;
    }

    private static void Restore(IReadOnlyDictionary<string, string> backups, IReadOnlySet<string> createdTargets)
    {
        foreach (var path in createdTargets)
            if (File.Exists(path)) File.Delete(path);
        foreach (var pair in backups)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
            File.Copy(pair.Value, pair.Key, overwrite: true);
        }
    }

    private static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
