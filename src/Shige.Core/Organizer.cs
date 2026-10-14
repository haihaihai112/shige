using System.Security.Cryptography;
namespace Shige.Core;

public class Organizer
{
    public class Entry
    {
        public string BatchId { get; set; } = "";
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Source { get; set; } = "";
        public string Destination { get; set; } = "";
        public string Staging { get; set; } = "";
        public string Hash { get; set; } = "";
        public string ExpectedHash { get; set; } = "";
        public string State { get; set; } = "Pending";
        public string? UndoOf
        {
            get; set;
        }
        public bool Undone
        {
            get; set;
        }
    }
    readonly string directory;
    readonly string journal;
    readonly SemaphoreSlim gate = new(1, 1);
    public Organizer(string dataDirectory)
    {
        directory = Path.GetFullPath(dataDirectory);
        PathPolicy.EnsureNoLinks(directory);
        Directory.CreateDirectory(directory);
        journal = Path.Combine(directory, "organizer.json");
    }
    List<Entry> Read() => File.Exists(journal) ? DurableJson.Read<List<Entry>>(journal) : [];
    void Save(List<Entry> entries) => DurableJson.Write(journal, entries);
    public bool CanUndo
    {
        get
        {
            try
            {
                return Read().Any(e => e.State == "Done" && !e.Undone && e.UndoOf == null);
            }
            catch { return false; }
        }
    }
    public List<PlanItem> Preview(string sourceDirectory, IReadOnlyDictionary<FileCategory, string> targets)
    {
        PathPolicy.EnsureNoLinks(sourceDirectory);
        var items = new List<PlanItem>();
        var reserved = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var source in Directory.EnumerateFiles(sourceDirectory).Order())
        {
            var a = File.GetAttributes(source);
            var ext = Path.GetExtension(source);
            if ((a & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0 || new[] { ".tmp", ".part", ".crdownload", ".download", ".partial" }.Contains(ext, StringComparer.OrdinalIgnoreCase) || Path.GetFileName(source).StartsWith("~$"))
                continue;
            var c = Classifier.GetCategory(source);
            if (!targets.TryGetValue(c, out var folder) || string.IsNullOrWhiteSpace(folder))
                continue;
            if (string.Equals(Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), PathPolicy.Comparison))
                continue;
            var target = PathPolicy.UniqueDestination(folder, Path.GetFileName(source));
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), PathPolicy.Comparison))
                continue;
            for (int n = 2; !reserved.Add(target); n++)
                target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(source)} ({n}){ext}");
            items.Add(new()
            {
                Source = source,
                Destination = target,
                Category = c,
                Note = Path.GetFileName(source) == Path.GetFileName(target) ? "" : "重名文件将使用新名称"
            });
        }
        return items;
    }
    static string Hash(string path)
    {
        PathPolicy.EnsureNoLinks(path);
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var main = Convert.ToHexString(SHA256.HashData(f));
        var streams = NativeSafety.AlternateStreams(path);
        if (streams.Count == 0)
            return main;
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        aggregate.AppendData(System.Text.Encoding.UTF8.GetBytes(main));
        foreach (var name in streams.Order(StringComparer.Ordinal))
        {
            aggregate.AppendData(System.Text.Encoding.UTF8.GetBytes(name));
            using var alternate = new FileStream(path + name, FileMode.Open, FileAccess.Read, FileShare.Read);
            aggregate.AppendData(SHA256.HashData(alternate));
        }
        return Convert.ToHexString(aggregate.GetHashAndReset());
    }
    // Each operation stages the source by atomic rename before copying. No deletion ever targets the original public name.
    void Complete(Entry e, List<Entry> all)
    {
        e.State = "Done";
        if (e.UndoOf != null)
        {
            var original = all.FirstOrDefault(x => x.Id == e.UndoOf);
            if (original != null)
                original.Undone = true;
        }
        Save(all);
    }
    void Advance(Entry e, List<Entry> all)
    {
        PathPolicy.EnsureNoLinks(e.Source);
        PathPolicy.EnsureNoLinks(e.Destination);
        PathPolicy.EnsureNoLinks(e.Staging);
        if (e.State == "Pending")
        {
            if (!File.Exists(e.Staging))
            {
                if (!File.Exists(e.Source))
                {
                    e.State = "Aborted";
                    Save(all);
                    throw new IOException("源文件不存在");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(e.Destination)!);
                File.Move(e.Source, e.Staging, false);
            }
            e.State = "Staged";
            Save(all);
        }
        if (e.State == "Renaming")
        {
            if (File.Exists(e.Staging))
            {
                e.State = "Staged";
                Save(all);
            }
            else if (File.Exists(e.Destination) && Hash(e.Destination) == e.Hash)
            {
                Complete(e, all);
                return;
            }
            else
                throw new IOException("原子移动状态不明确，请检查目标文件: " + e.Destination);
        }
        if (e.State == "Staged")
        {
            e.Hash = Hash(e.Staging);
            if (e.ExpectedHash.Length > 0 && e.Hash != e.ExpectedHash)
                throw new IOException("源文件已变化，不能继续撤销");
            Save(all);
            var temp = e.Destination + ".shige-" + e.Id + ".tmp";
            if (File.Exists(temp))
                throw new IOException("保留未完成复制文件: " + temp);
            if (File.Exists(e.Destination) || Directory.Exists(e.Destination))
                throw new IOException("目标已被占用");
            if (NativeSafety.SameVolume(e.Staging, e.Destination))
            {
                e.State = "Renaming";
                Save(all);
                File.Move(e.Staging, e.Destination, false);
                if (Hash(e.Destination) != e.Hash)
                    throw new IOException("移动期间内容变化，请检查目标文件: " + e.Destination);
                Complete(e, all);
                return;
            }
            NativeSafety.RejectAlternateStreams(e.Staging);
            using var source = NativeSafety.OpenForVerifiedDeletion(e.Staging);
            if (Convert.ToHexString(SHA256.HashData(source)) != e.Hash)
                throw new IOException("源文件已变化");
            source.Position = 0;
            var creation = File.GetCreationTimeUtc(e.Staging);
            var modified = File.GetLastWriteTimeUtc(e.Staging);
            var accessed = File.GetLastAccessTimeUtc(e.Staging);
            using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                source.CopyTo(target);
                target.Flush(true);
                target.Position = 0;
                if (Convert.ToHexString(SHA256.HashData(target)) != e.Hash)
                    throw new IOException("复制校验失败");
            }
            File.SetAttributes(temp, File.GetAttributes(e.Staging) & ~FileAttributes.ReparsePoint);
            File.SetCreationTimeUtc(temp, creation);
            File.SetLastWriteTimeUtc(temp, modified);
            File.SetLastAccessTimeUtc(temp, accessed);
            if (File.GetCreationTimeUtc(temp) != creation || File.GetLastWriteTimeUtc(temp) != modified)
                throw new IOException("目标文件系统不能精确保留时间，源文件保留");
            File.Move(temp, e.Destination, false);
            e.State = "Copied";
            Save(all);
            using var destinationGuard = new FileStream(e.Destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(SHA256.HashData(destinationGuard)) != e.Hash)
                throw new IOException("目标已变化，保留源文件");
            NativeSafety.DeleteOpened(source);
            Complete(e, all);
            return;
        }
        if (e.State == "Copied")
        {
            using var destinationGuard = new FileStream(e.Destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(SHA256.HashData(destinationGuard)) != e.Hash)
                throw new IOException("目标已变化，保留暂存源文件");
            if (File.Exists(e.Staging))
            {
                NativeSafety.RejectAlternateStreams(e.Staging);
                using var source = NativeSafety.OpenForVerifiedDeletion(e.Staging);
                if (Convert.ToHexString(SHA256.HashData(source)) != e.Hash)
                    throw new IOException("暂存文件已变化");
                NativeSafety.DeleteOpened(source);
            }
            Complete(e, all);
        }
    }
    bool Rollback(Entry e, List<Entry> all)
    {
        if (e.State is not ("Pending" or "Staged" or "Renaming"))
            return false;
        PathPolicy.EnsureNoLinks(e.Staging);
        PathPolicy.EnsureNoLinks(e.Source);
        // A synchronous failure before staging is terminal, not an interrupted move.
        // Keeping it Pending would replay a reported failure and block the rest of the batch.
        if (e.State == "Pending" && !File.Exists(e.Staging) && File.Exists(e.Source))
        {
            e.State = "Aborted";
            Save(all);
            return true;
        }
        if (!File.Exists(e.Staging) || File.Exists(e.Source) || Directory.Exists(e.Source))
            return false;
        File.Move(e.Staging, e.Source, false);
        e.State = "Aborted";
        Save(all);
        return true;
    }
    Entry NewEntry(string source, string destination, string? undoOf = null)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        PathPolicy.EnsureNoLinks(source);
        PathPolicy.EnsureNoLinks(destination);
        if (string.Equals(source, destination, PathPolicy.Comparison))
            throw new IOException("源与目标相同");
        return new()
        {
            Source = source,
            Destination = destination,
            Staging = Path.Combine(Path.GetDirectoryName(source)!, ".shige-staging-" + Guid.NewGuid().ToString("N")),
            UndoOf = undoOf
        };
    }
    async Task<OrganizationResult> Run(Func<List<Entry>, OrganizationResult> action)
    {
        await gate.WaitAsync();
        try
        {
            using var lease = new FileStream(Path.Combine(directory, "organizer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return await Task.Run(() => action(Read())).ConfigureAwait(false);
        }
        catch (Exception ex) { return new() { Failed = 1, Messages = [ex.Message] }; }
        finally { gate.Release(); }
    }
    public Task<OrganizationResult> ExecuteAsync(IEnumerable<PlanItem> items, IProgress<OrganizationProgress>? progress = null, CancellationToken cancellationToken = default) => Run(all =>
    {
        var result = new OrganizationResult();
        if (all.Any(e => e.State != "Done" && e.State != "Aborted"))
        {
            result.Failed++;
            result.Messages.Add("请先恢复未完成的整理。");
            return result;
        }
        var batchId = Guid.NewGuid().ToString("N");
        var selected = items.Where(i => i.Included).ToList();
        foreach (var item in selected)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.Cancelled = true;
                break;
            }
            Entry? entry = null;
            try
            {
                var destination = PathPolicy.UniqueDestination(Path.GetDirectoryName(Path.GetFullPath(item.Destination))!, Path.GetFileName(item.Destination));
                item.Destination = destination;
                entry = NewEntry(item.Source, destination);
                entry.BatchId = batchId;
                all.Add(entry);
                Save(all);
                Advance(entry, all);
                result.Completed++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Messages.Add(item.Source + ": " + ex.Message);
                if (entry != null)
                    TryRollback(entry, all, result);
            }
            progress?.Report(new()
            {
                Completed = result.Completed + result.Failed,
                Total = selected.Count,
                Current = item.Source
            });
        }
        return result;
    });
    public Task<OrganizationResult> UndoAsync(IProgress<OrganizationProgress>? progress = null, CancellationToken cancellationToken = default) => Run(all =>
    {
        var result = new OrganizationResult();
        if (all.Any(e => e.State != "Done" && e.State != "Aborted"))
        {
            result.Failed++;
            result.Messages.Add("请先恢复未完成的整理。");
            return result;
        }
        var latestBatch = all.LastOrDefault(e => e.State == "Done" && !e.Undone && e.UndoOf == null)?.BatchId;
        var entries = all.Where(e => e.State == "Done" && !e.Undone && e.UndoOf == null && e.BatchId == latestBatch).Reverse().ToList();
        foreach (var original in entries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.Cancelled = true;
                break;
            }
            Entry? entry = null;
            try
            {
                if (File.Exists(original.Source) || Directory.Exists(original.Source))
                    throw new IOException("原位置已被占用");
                if (Hash(original.Destination) != original.Hash)
                    throw new IOException("文件内容已变化，不能撤销");
                entry = NewEntry(original.Destination, original.Source, original.Id);
                entry.ExpectedHash = original.Hash;
                all.Add(entry);
                Save(all);
                Advance(entry, all);
                result.Completed++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Messages.Add(ex.Message);
                if (entry != null)
                    TryRollback(entry, all, result);
            }
            progress?.Report(new()
            {
                Completed = result.Completed + result.Failed,
                Total = entries.Count,
                Current = original.Destination
            });
        }
        return result;
    });
    void TryRollback(Entry entry, List<Entry> all, OrganizationResult result)
    {
        try
        {
            if (Rollback(entry, all))
                result.Messages.Add("已安全恢复原位置: " + entry.Source);
            else if (File.Exists(entry.Staging))
                result.Messages.Add("暂存文件保留，请执行恢复: " + entry.Staging);
        }
        catch (Exception ex) { result.Messages.Add("恢复原位置失败，暂存文件保留: " + entry.Staging + "；" + ex.Message); }
    }
    public Task<OrganizationResult> RecoverAsync() => Run(all =>
    {
        var result = new OrganizationResult();
        foreach (var e in all.Where(e => e.State != "Done" && e.State != "Aborted").ToList())
        {
            try
            {
                // An unjournaled published destination cannot be proven ours; retain the staged source for manual recovery.

                Advance(e, all);
                result.Completed++;
            }
            catch (Exception ex) { result.Failed++; bool rolledBack = false; try { rolledBack = Rollback(e, all); } catch (Exception rollback) { result.Messages.Add(rollback.Message); } result.Messages.Add(rolledBack ? "已安全恢复原位置，目标与复制临时文件保留: " + e.Source + "；" + ex.Message : (File.Exists(e.Staging) ? "请先将原位置现有文件另存到其他目录，保持暂存文件不动，再重试恢复。原位置: " + e.Source + "；暂存: " + e.Staging : "未找到暂存文件，请检查目标文件并联系维护者核对事务日志。目标: " + e.Destination) + "；" + ex.Message); }
        }
        return result;
    });
}










