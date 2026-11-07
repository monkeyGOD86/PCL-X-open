namespace PCLCS.PackSync;

public sealed class PackSyncJournalEntry {
    public string Path { get; set; } = "";
    public string? BeforeSha512 { get; set; }
    public string? AfterSha512 { get; set; }
    public string? BackupName { get; set; }
    // Advisory progress only: recovery always checks the actual file hashes.
    public string Step { get; set; } = "pending";
}

public sealed class PackSyncJournal {
    public int SchemaVersion { get; set; } = 1;
    public string TransactionId { get; set; } = "";
    public string Status { get; set; } = "preparing";
    public bool PreviousStateExists { get; set; }
    public string? PreviousStateSha512 { get; set; }
    public string? NewStateSha512 { get; set; }
    public bool UndoRequested { get; set; }
    public string? UndoStateSha512 { get; set; }
    public List<PackSyncJournalEntry> Entries { get; set; } = new();
}

public sealed class PackSyncPendingTransaction {
    public string TransactionId { get; internal set; } = "";
    public string? Status { get; internal set; }
    public string? Error { get; internal set; }
}

/// <summary>Creates the durable backup material needed before an operation can modify game files.</summary>
public static class PackSyncTransaction {
    /// <summary>Find transactions requiring attention. Damaged records remain visible and block new work.</summary>
    public static IReadOnlyList<PackSyncPendingTransaction> FindPending(string instanceDirectory) {
        string folder = Path.Combine(Path.GetFullPath(instanceDirectory), "PCL", "PackSync", "transactions");
        CheckPath(folder);
        if (!Directory.Exists(folder)) return Array.Empty<PackSyncPendingTransaction>();
        var result = new List<PackSyncPendingTransaction>();
        foreach (string directory in Directory.EnumerateDirectories(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
            string id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _)) {
                result.Add(new PackSyncPendingTransaction { TransactionId = id, Error = "事务目录名称无效。" });
                continue;
            }
            try {
                var journal = Inspect(instanceDirectory, id);
                if (journal.Status != "completed" && journal.Status != "restored" && journal.Status != "abandoned")
                    result.Add(new PackSyncPendingTransaction { TransactionId = id, Status = journal.Status });
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException) {
                result.Add(new PackSyncPendingTransaction { TransactionId = id, Error = ex.Message });
            }
        }
        return result.AsReadOnly();
    }

    /// <summary>Remove only recognized, older completed recovery points after a new commit.</summary>
    public static IReadOnlyList<string> PruneCompleted(string instanceDirectory, string currentTransactionId) {
        var current = Inspect(instanceDirectory, currentTransactionId);
        string root = Path.GetFullPath(instanceDirectory);
        string state = Path.Combine(root, "PCL", "PackSync", "state.json");
        if (current.Status != "completed" || current.NewStateSha512 == null || !HashEquals(CurrentHash(state), current.NewStateSha512))
            throw new InvalidOperationException("当前恢复点尚未完整提交，不能清理旧备份。");
        string parent = Path.GetFullPath(Path.Combine(root, "PCL", "PackSync", "transactions"));
        CheckPath(parent);
        var skipped = new List<string>();
        foreach (string raw in Directory.EnumerateDirectories(parent)) {
            string folder = Path.GetFullPath(raw);
            string id = Path.GetFileName(folder);
            if (!folder.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(id, "N", out _) || string.Equals(id, currentTransactionId, StringComparison.OrdinalIgnoreCase)) continue;
            try {
                CheckPath(folder);
                var journal = Inspect(root, id);
                if (journal.Status != "completed") continue;
                string backup = Path.Combine(folder, "backup");
                CheckPath(backup);
                var expectedRoot = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "journal.json", "backup" };
                if (journal.PreviousStateExists) expectedRoot.Add("previous-state.json");
                if (!Directory.Exists(backup) || Directory.EnumerateFileSystemEntries(folder).Any(p => !expectedRoot.Contains(Path.GetFileName(p)))) { skipped.Add(id); continue; }
                var expectedFiles = new HashSet<string>(journal.Entries.Where(e => e.BackupName != null).Select(e => e.BackupName!), StringComparer.OrdinalIgnoreCase);
                var files = Directory.EnumerateFileSystemEntries(backup).ToArray();
                if (files.Length != expectedFiles.Count || files.Any(p => !expectedFiles.Contains(Path.GetFileName(p)) || Directory.Exists(p))) { skipped.Add(id); continue; }
                foreach (string file in files) CheckPath(file);
                // Move the checked directory out of the active transaction set first. A failed
                // cleanup cannot leave a half-deleted journal blocking future syncs.
                string trashRoot = Path.GetFullPath(Path.Combine(root, "PCL", "PackSync", "pruned"));
                CheckPath(trashRoot);
                Directory.CreateDirectory(trashRoot);
                CheckPath(trashRoot);
                string destination = Path.GetFullPath(Path.Combine(trashRoot, id));
                if (!destination.StartsWith(trashRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    Directory.Exists(destination) || File.Exists(destination)) { skipped.Add(id); continue; }
                Directory.Move(folder, destination);
                folder = destination;
                backup = Path.Combine(folder, "backup");
                files = files.Select(p => Path.Combine(backup, Path.GetFileName(p))).ToArray();
                foreach (string file in files) File.Delete(file);
                Directory.Delete(backup);
                if (journal.PreviousStateExists) File.Delete(Path.Combine(folder, "previous-state.json"));
                File.Delete(Path.Combine(folder, "journal.json"));
                Directory.Delete(folder);
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException) {
                skipped.Add(id);
            }
        }
        return skipped.AsReadOnly();
    }
    /// <remarks>The caller must hold the instance lock and check that the game is not running.</remarks>
    public static PackSyncJournal Backup(string archivePath, string instanceDirectory, string gameDirectory,
        IDictionary<string, string> environment, PackSyncOperationPlan plan, string transactionId) {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (!Guid.TryParseExact(transactionId, "N", out _)) throw new InvalidDataException("事务标识无效。");
        plan.ValidateStillCurrent(archivePath, instanceDirectory, gameDirectory, environment);
        string root = Path.GetFullPath(instanceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string folder = Path.Combine(root, "PCL", "PackSync", "transactions", transactionId);
        CheckPath(folder);
        if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException("事务目录已存在。");
        Directory.CreateDirectory(folder);
        CheckPath(folder);
        string backupFolder = Path.Combine(folder, "backup");
        Directory.CreateDirectory(backupFolder);
        var journal = new PackSyncJournal { TransactionId = transactionId };
        int i = 0;
        foreach (var change in plan.Changes) {
            PackSyncPlanner.ValidatePath(change.Path);
            journal.Entries.Add(new PackSyncJournalEntry { Path = change.Path, BeforeSha512 = change.BeforeSha512,
                AfterSha512 = change.AfterSha512, BackupName = change.BeforeSha512 == null ? null : (++i).ToString("D8") + ".data" });
        }
        string statePath = Path.Combine(root, "PCL", "PackSync", "state.json");
        CheckPath(statePath);
        journal.PreviousStateExists = File.Exists(statePath);
        if (journal.PreviousStateExists) {
            using var stateStream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            journal.PreviousStateSha512 = PackSyncReader.Hash(stateStream);
        }
        // Persist the full plan before copying anything; an interrupted preparation never changes game files.
        WriteJournal(folder, journal);
        if (journal.PreviousStateExists) {
            string previous = Path.Combine(folder, "previous-state.json");
            File.Copy(statePath, previous, false);
            using var saved = new FileStream(previous, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!string.Equals(PackSyncReader.Hash(saved), journal.PreviousStateSha512, StringComparison.OrdinalIgnoreCase))
                throw new IOException("旧状态备份校验失败。");
        }
        foreach (var entry in journal.Entries) {
            string target = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            CheckPath(target);
            if (entry.BeforeSha512 == null) {
                if (File.Exists(target) || Directory.Exists(target)) throw new IOException($"文件在预览后出现：{entry.Path}");
                continue;
            }
            string backup = Path.Combine(backupFolder, entry.BackupName!);
            using (var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                input.CopyTo(output);
                output.Flush(true);
            }
            using var verify = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!string.Equals(PackSyncReader.Hash(verify), entry.BeforeSha512, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"备份校验失败，文件可能已变化：{entry.Path}");
        }
        plan.ValidateStillCurrent(archivePath, instanceDirectory, gameDirectory, environment);
        journal.Status = "backedUp";
        WriteJournal(folder, journal);
        return journal;
    }

    /// <summary>Read and validate transaction metadata and available backups without changing any files.</summary>
    public static PackSyncJournal Inspect(string instanceDirectory, string transactionId) {
        if (!Guid.TryParseExact(transactionId, "N", out _)) throw new InvalidDataException("事务标识无效。");
        string folder = Path.Combine(Path.GetFullPath(instanceDirectory), "PCL", "PackSync", "transactions", transactionId);
        string file = Path.Combine(folder, "journal.json");
        CheckPath(file);
        if (!File.Exists(file) || new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("事务记录缺失或过大。");
        JObject obj;
        try {
            using var input = File.OpenText(file);
            using var reader = new JsonTextReader(input) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
            obj = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        } catch (JsonException ex) { throw new InvalidDataException("事务记录损坏。", ex); }
        if ((int?)obj["SchemaVersion"] != 1 || (string?)obj["TransactionId"] != transactionId ||
            obj["Entries"] is not JArray || obj["PreviousStateExists"]?.Type != JTokenType.Boolean)
            throw new InvalidDataException("事务记录格式无效。");
        var journal = obj.ToObject<PackSyncJournal>() ?? throw new InvalidDataException("事务记录为空。");
        if (journal.Status != "preparing" && journal.Status != "backedUp" && journal.Status != "applying" && journal.Status != "completed" && journal.Status != "restoring" && journal.Status != "restored" && journal.Status != "abandoned")
            throw new InvalidDataException("事务状态不受支持。");
        if (journal.PreviousStateExists != (journal.PreviousStateSha512 != null) ||
            (journal.PreviousStateSha512 != null && !ValidHash(journal.PreviousStateSha512)) ||
            (journal.NewStateSha512 != null && !ValidHash(journal.NewStateSha512)) ||
            (journal.UndoStateSha512 != null && !ValidHash(journal.UndoStateSha512)) ||
            (journal.UndoRequested && journal.PreviousStateExists && journal.UndoStateSha512 == null))
            throw new InvalidDataException("事务旧状态记录无效。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var backups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in journal.Entries) {
            if (entry == null || !paths.Add(PackSyncPlanner.ValidatePath(entry.Path)) ||
                (entry.BeforeSha512 != null && !ValidHash(entry.BeforeSha512)) ||
                (entry.AfterSha512 != null && !ValidHash(entry.AfterSha512)) ||
                (entry.BeforeSha512 == null && entry.AfterSha512 == null) ||
                (entry.Step != "pending" && entry.Step != "applied" && entry.Step != "restored")) throw new InvalidDataException("事务文件记录无效。");
            if (entry.BeforeSha512 == null) {
                if (entry.BackupName != null) throw new InvalidDataException("事务备份名称无效。");
            } else {
                if (entry.BackupName == null || !System.Text.RegularExpressions.Regex.IsMatch(entry.BackupName, @"\A[0-9]{8}\.data\z") || !backups.Add(entry.BackupName))
                    throw new InvalidDataException("事务备份名称无效。");
                if (journal.Status != "preparing" && journal.Status != "abandoned") VerifyBackup(Path.Combine(folder, "backup", entry.BackupName), entry.BeforeSha512);
            }
        }
        if (journal.PreviousStateExists && journal.Status != "preparing" && journal.Status != "abandoned")
            VerifyBackup(Path.Combine(folder, "previous-state.json"), journal.PreviousStateSha512!);
        if (journal.UndoRequested && journal.PreviousStateExists)
            VerifyBackup(Path.Combine(folder, "undo-state.json"), journal.UndoStateSha512!);
        return journal;
    }

    /// <summary>Mark an interrupted backup preparation as abandoned; no game files have been changed at that stage.</summary>
    public static void AbandonPreparation(string instanceDirectory, string transactionId) {
        var journal = Inspect(instanceDirectory, transactionId);
        if (journal.Status != "preparing") throw new InvalidOperationException("该事务已进入文件修改阶段，不能放弃准备。");
        string folder = Path.Combine(Path.GetFullPath(instanceDirectory), "PCL", "PackSync", "transactions", transactionId);
        journal.Status = "abandoned";
        WriteJournal(folder, journal);
    }

    /// <summary>Apply verified staged files after backup. The caller must hold the instance lock and stop the game.</summary>
    /// <remarks>Call CommitState after successful application to finish the transaction.</remarks>
    public static void ApplyFiles(string archivePath, string instanceDirectory, string gameDirectory,
        IDictionary<string, string> environment, PackSyncOperationPlan plan, string transactionId,
        IEnumerable<PackSyncPreparedFile> stagedFiles) =>
        ApplyFiles(archivePath, instanceDirectory, gameDirectory, environment, plan, transactionId, stagedFiles,
            System.Threading.CancellationToken.None, null);

    public static void ApplyFiles(string archivePath, string instanceDirectory, string gameDirectory,
        IDictionary<string, string> environment, PackSyncOperationPlan plan, string transactionId,
        IEnumerable<PackSyncPreparedFile> stagedFiles, System.Threading.CancellationToken cancellationToken, Action? onRestore) {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (stagedFiles == null) throw new ArgumentNullException(nameof(stagedFiles));
        var journal = Inspect(instanceDirectory, transactionId);
        if (journal.Status != "backedUp") throw new InvalidOperationException("事务尚未完成备份或已开始应用。");
        var changes = plan.Changes.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        if (changes.Count != journal.Entries.Count || journal.Entries.Any(e => !changes.TryGetValue(e.Path, out var c) ||
            !HashEquals(e.BeforeSha512, c.BeforeSha512) || !HashEquals(e.AfterSha512, c.AfterSha512)))
            throw new InvalidDataException("事务记录与已确认计划不一致。");
        var staged = new Dictionary<string, PackSyncPreparedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in stagedFiles) {
            string path = PackSyncPlanner.ValidatePath(item.TargetPath);
            if (staged.ContainsKey(path) || !changes.TryGetValue(path, out var change) || change.AfterSha512 == null ||
                !HashEquals(change.AfterSha512, item.Sha512)) throw new InvalidDataException($"暂存文件不符合计划：{path}");
            VerifyBackup(item.StagedPath, change.AfterSha512);
            staged.Add(path, item);
        }
        if (staged.Count != changes.Values.Count(c => c.AfterSha512 != null)) throw new InvalidDataException("暂存文件不完整。");
        plan.ValidateStillCurrent(archivePath, instanceDirectory, gameDirectory, environment);
        string root = Path.GetFullPath(instanceDirectory);
        string folder = Path.Combine(root, "PCL", "PackSync", "transactions", transactionId);
        journal.Status = "applying";
        WriteJournal(folder, journal);
        try {
            foreach (var entry in journal.Entries) {
                cancellationToken.ThrowIfCancellationRequested();
                string target = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!HashEquals(CurrentHash(target), entry.BeforeSha512)) throw new IOException($"文件在应用过程中发生变化：{entry.Path}");
                if (entry.AfterSha512 == null) {
                    File.Delete(target);
                } else {
                    string parent = Path.GetDirectoryName(target)!;
                    CheckPath(parent);
                    Directory.CreateDirectory(parent);
                    string temp = Path.Combine(parent, ".pcl-sync-" + Guid.NewGuid().ToString("N"));
                    try {
                        File.Copy(staged[entry.Path].StagedPath, temp, false);
                        VerifyBackup(temp, entry.AfterSha512);
                        if (!HashEquals(CurrentHash(target), entry.BeforeSha512)) throw new IOException($"文件在应用过程中发生变化：{entry.Path}");
                        if (entry.BeforeSha512 == null) File.Move(temp, target); else File.Replace(temp, target, null);
                    } finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                if (!HashEquals(CurrentHash(target), entry.AfterSha512)) throw new IOException($"应用结果校验失败：{entry.Path}");
                entry.Step = "applied";
                WriteJournal(folder, journal);
            }
            cancellationToken.ThrowIfCancellationRequested();
        } catch (Exception applyError) {
            try { onRestore?.Invoke(); } catch { /* Reporting must not prevent recovery. */ }
            try { RestoreInterrupted(instanceDirectory, transactionId); }
            catch (Exception restoreError) { throw new AggregateException("同步失败且恢复未完成，事务材料已保留。", applyError, restoreError); }
            throw;
        }
    }

    /// <summary>Commit the team baseline only after all confirmed file changes have been applied.</summary>
    public static void CommitState(string instanceDirectory, string transactionId, PackSyncOperationPlan plan, PackSyncPackage package) {
        if (plan == null || package == null) throw new ArgumentNullException(plan == null ? nameof(plan) : nameof(package));
        var journal = Inspect(instanceDirectory, transactionId);
        if (journal.Status != "applying" || package.PackId != plan.PackId || !HashEquals(package.ArchiveSha512, plan.ArchiveSha512))
            throw new InvalidDataException("事务或整合包与计划不一致。");
        var changes = plan.Changes.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
        if (journal.Entries.Count != changes.Count || journal.Entries.Any(e => !changes.TryGetValue(e.Path, out var c) ||
            !HashEquals(e.BeforeSha512, c.BeforeSha512) || !HashEquals(e.AfterSha512, c.AfterSha512)))
            throw new InvalidDataException("事务记录与计划不一致。");
        string root = Path.GetFullPath(instanceDirectory);
        string folder = Path.Combine(root, "PCL", "PackSync", "transactions", transactionId);
        string statePath = Path.Combine(root, "PCL", "PackSync", "state.json");
        if (!HashEquals(CurrentHash(statePath), journal.PreviousStateSha512)) throw new IOException("小队状态已变化。");
        foreach (var entry in journal.Entries)
            if (!HashEquals(CurrentHash(Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar))), entry.AfterSha512))
                throw new IOException($"应用结果已变化：{entry.Path}");
        var state = new PackSyncState { SchemaVersion = 1, PackId = plan.PackId, ReleaseId = package.ReleaseId,
            ArchiveSha512 = plan.ArchiveSha512, Dependencies = package.Dependencies.ToDictionary(p => p.Key, p => p.Value),
            BaselineFiles = plan.TargetBaseline.ToDictionary(p => p.Key, p => p.Value),
            OptionalSelections = plan.OptionalSelections.ToList(), LastTransactionId = transactionId };
        byte[] content = new System.Text.UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(state, Formatting.Indented));
        using (var data = new MemoryStream(content)) journal.NewStateSha512 = PackSyncReader.Hash(data);
        WriteJournal(folder, journal); // The new hash must be durable before replacing state.json.
        string temp = Path.Combine(Path.GetDirectoryName(statePath)!, "state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(content, 0, content.Length); output.Flush(true); }
            if (!HashEquals(CurrentHash(statePath), journal.PreviousStateSha512)) throw new IOException("小队状态在提交期间发生变化。");
            if (journal.PreviousStateExists) File.Replace(temp, statePath, null); else File.Move(temp, statePath);
            if (!HashEquals(CurrentHash(statePath), journal.NewStateSha512)) throw new IOException("小队状态校验失败。");
            journal.Status = "completed";
            WriteJournal(folder, journal);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>Undo the most recent completed transaction; callers must hold the instance lock and stop the game.</summary>
    public static void Undo(string instanceDirectory, string transactionId) {
        var journal = Inspect(instanceDirectory, transactionId);
        if (journal.Status != "completed" || journal.NewStateSha512 == null) throw new InvalidOperationException("该事务不能撤销。");
        string root = Path.GetFullPath(instanceDirectory);
        string folder = Path.Combine(root, "PCL", "PackSync", "transactions", transactionId);
        string state = Path.Combine(root, "PCL", "PackSync", "state.json");
        if (!HashEquals(CurrentHash(state), journal.NewStateSha512)) throw new IOException("小队状态已变化，不能自动撤销。");
        foreach (var entry in journal.Entries)
            if (!HashEquals(CurrentHash(Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar))), entry.AfterSha512))
                throw new IOException($"文件已变化，不能自动撤销：{entry.Path}");
        if (journal.PreviousStateExists) {
            string previousPath = Path.Combine(folder, "previous-state.json");
            byte[] bytes;
            try {
                var previous = JObject.Parse(File.ReadAllText(previousPath));
                previous["lastTransactionId"] = JValue.CreateNull();
                bytes = new System.Text.UTF8Encoding(false).GetBytes(previous.ToString(Formatting.Indented));
            } catch (JsonException) {
                // A damaged state used an empty baseline; undo preserves its original bytes.
                bytes = File.ReadAllBytes(previousPath);
            }
            using (var hashInput = new MemoryStream(bytes)) journal.UndoStateSha512 = PackSyncReader.Hash(hashInput);
            string undoFile = Path.Combine(folder, "undo-state.json");
            if (File.Exists(undoFile)) VerifyBackup(undoFile, journal.UndoStateSha512);
            else using (var output = new FileStream(undoFile, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
        }
        journal.UndoRequested = true;
        journal.Status = "restoring";
        WriteJournal(folder, journal);
        RestoreInterrupted(instanceDirectory, transactionId);
    }

    /// <summary>Restore an interrupted transaction; callers must hold the instance lock and stop the game.</summary>
    public static void RestoreInterrupted(string instanceDirectory, string transactionId) {
        var journal = Inspect(instanceDirectory, transactionId);
        if (journal.Status != "backedUp" && journal.Status != "applying" && journal.Status != "restoring")
            throw new InvalidOperationException("该事务不支持中断恢复。");
        string root = Path.GetFullPath(instanceDirectory);
        string folder = Path.Combine(root, "PCL", "PackSync", "transactions", transactionId);
        string state = Path.Combine(root, "PCL", "PackSync", "state.json");
        string? restoredStateHash = journal.UndoRequested ? journal.UndoStateSha512 : journal.PreviousStateSha512;
        if (!HashEquals(CurrentHash(state), restoredStateHash) &&
            (journal.NewStateSha512 == null || !HashEquals(CurrentHash(state), journal.NewStateSha512)))
            throw new IOException("小队状态已变化，不能自动恢复；事务备份已保留。");
        // Check all paths before changing any of them. A file may already have been restored.
        foreach (var entry in journal.Entries) {
            string path = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            string? actual = CurrentHash(path);
            if (journal.Status == "backedUp" && !HashEquals(actual, entry.BeforeSha512))
                throw new IOException($"文件在备份后发生变化：{entry.Path}");
            if (!HashEquals(actual, entry.BeforeSha512) && !HashEquals(actual, entry.AfterSha512))
                throw new IOException($"文件已被修改，不能自动恢复：{entry.Path}");
        }
        journal.Status = "restoring";
        WriteJournal(folder, journal);
        foreach (var entry in journal.Entries.AsEnumerable().Reverse()) {
            string path = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            string? actual = CurrentHash(path);
            if (HashEquals(actual, entry.BeforeSha512)) {
                if (entry.Step != "restored") { entry.Step = "restored"; WriteJournal(folder, journal); }
                continue;
            }
            if (!HashEquals(actual, entry.AfterSha512)) throw new IOException($"文件在恢复过程中发生变化：{entry.Path}");
            if (entry.BeforeSha512 == null) {
                File.Delete(path);
            } else {
                string parent = Path.GetDirectoryName(path)!;
                CheckPath(parent);
                Directory.CreateDirectory(parent);
                string temp = Path.Combine(parent, ".pcl-restore-" + Guid.NewGuid().ToString("N"));
                try {
                    File.Copy(Path.Combine(folder, "backup", entry.BackupName!), temp, false);
                    VerifyBackup(temp, entry.BeforeSha512);
                    if (!HashEquals(CurrentHash(path), entry.AfterSha512)) throw new IOException($"文件在恢复过程中发生变化：{entry.Path}");
                    if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                } finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            if (!HashEquals(CurrentHash(path), entry.BeforeSha512)) throw new IOException($"文件恢复校验失败：{entry.Path}");
            entry.Step = "restored";
            WriteJournal(folder, journal);
        }
        if (!HashEquals(CurrentHash(state), restoredStateHash)) {
            if (!HashEquals(CurrentHash(state), journal.NewStateSha512)) throw new IOException("小队状态在恢复期间发生变化。");
            if (!journal.PreviousStateExists) File.Delete(state);
            else {
                string temp = Path.Combine(Path.GetDirectoryName(state)!, "state-restore-" + Guid.NewGuid().ToString("N") + ".tmp");
                try {
                    File.Copy(Path.Combine(folder, journal.UndoRequested ? "undo-state.json" : "previous-state.json"), temp, false);
                    VerifyBackup(temp, restoredStateHash!);
                    if (!HashEquals(CurrentHash(state), journal.NewStateSha512)) throw new IOException("小队状态在恢复期间发生变化。");
                    File.Replace(temp, state, null);
                } finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
        journal.Status = "restored";
        WriteJournal(folder, journal);
    }

    private static string? CurrentHash(string path) {
        CheckPath(path);
        if (Directory.Exists(path)) throw new IOException($"文件路径被文件夹占用：{path}");
        if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return PackSyncReader.Hash(input);
    }
    private static bool HashEquals(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool ValidHash(string hash) => hash.Length == 128 && hash.All(Uri.IsHexDigit);
    private static void VerifyBackup(string path, string expected) {
        CheckPath(path);
        if (!File.Exists(path)) throw new InvalidDataException($"事务备份缺失：{path}");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(PackSyncReader.Hash(input), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"事务备份校验失败：{path}");
    }

    private static void WriteJournal(string folder, PackSyncJournal journal) {
        string target = Path.Combine(folder, "journal.json");
        string temp = Path.Combine(folder, "journal-" + Guid.NewGuid().ToString("N") + ".tmp");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false))) {
            writer.Write(JsonConvert.SerializeObject(journal, Formatting.Indented));
            writer.Flush();
            stream.Flush(true);
        }
        if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
    }

    private static void CheckPath(string path) {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current)) {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"路径包含链接：{current}");
            current = Path.GetDirectoryName(current);
        }
    }
}
