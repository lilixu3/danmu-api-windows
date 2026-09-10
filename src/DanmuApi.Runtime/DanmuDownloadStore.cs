using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DanmuApi.Runtime;

public sealed record DanmuDownloadRecordEnvelope(IReadOnlyList<DanmuDownloadRecord> Records);
public sealed record DanmuDownloadTaskEnvelope(IReadOnlyList<DanmuDownloadTask> Tasks);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DanmuDownloadSettings))]
[JsonSerializable(typeof(DanmuDownloadRecordEnvelope))]
[JsonSerializable(typeof(DanmuDownloadTaskEnvelope))]
internal sealed partial class DanmuDownloadJsonContext : JsonSerializerContext;

/// <summary>
/// 弹幕下载本地仓库：设置、下载记录（≤500）与下载队列（≤1200）的原子 JSON 持久化。
/// </summary>
public sealed class DanmuDownloadStore
{
    private readonly object _sync = new();
    private readonly string _settingsFile;
    private readonly string _recordsFile;
    private readonly string _queueFile;
    private DanmuDownloadSettings _settings = new();
    private List<DanmuDownloadRecord> _records = [];
    private List<DanmuDownloadTask> _tasks = [];
    private long _nextRecordId;
    private long _nextTaskId;

    /// <summary>持久化失败诊断；UI 必须展示，不允许静默丢弃。</summary>
    public event Action<string>? PersistFailed;

    /// <summary>构造期数据文件读取失败诊断（事件在构造期间无人订阅，故单独保留）。</summary>
    public IReadOnlyList<string> StartupDiagnostics => _startupDiagnostics.ToArray();

    private readonly List<string> _startupDiagnostics = [];

    public DanmuDownloadStore(string settingsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        _settingsFile = Path.Combine(settingsDirectory, "download", "settings.json");
        _recordsFile = Path.Combine(settingsDirectory, "download", "records.json");
        _queueFile = Path.Combine(settingsDirectory, "download", "queue.json");
        _settings = Load(_settingsFile, () => new DanmuDownloadSettings());
        _records = Load(_recordsFile, () => new DanmuDownloadRecordEnvelope([])).Records.ToList();
        _tasks = Load(_queueFile, () => new DanmuDownloadTaskEnvelope([])).Tasks.ToList();
        _nextRecordId = _records.Count == 0 ? 0 : _records.Max(record => record.Id);
        _nextTaskId = _tasks.Count == 0 ? 0 : _tasks.Max(task => task.TaskId);    }

    public DanmuDownloadSettings Settings
    {
        get
        {
            lock (_sync)
            {
                return _settings;
            }
        }
    }

    public IReadOnlyList<DanmuDownloadRecord> Records
    {
        get
        {
            lock (_sync)
            {
                return _records.OrderByDescending(record => record.CreatedAt).ThenByDescending(record => record.Id).ToArray();
            }
        }
    }

    public IReadOnlyList<DanmuDownloadTask> QueueTasks
    {
        get
        {
            lock (_sync)
            {
                return _tasks.ToArray();
            }
        }
    }

    public void SaveSettings(DanmuDownloadSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            _settings = settings;
            WriteAtomic(_settingsFile, settings);
        }
    }

    public DanmuDownloadRecord AppendRecord(DanmuDownloadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_sync)
        {
            var id = record.Id > 0 ? record.Id : ++_nextRecordId;
            if (id > _nextRecordId)
            {
                _nextRecordId = id;
            }

            var appended = record.Id == id
                ? record
                : record with { Id = id };
            _records.Add(appended);
            if (_records.Count > DanmuDownloadDefaults.MaximumRecords)
            {
                _records = _records
                    .OrderByDescending(item => item.CreatedAt)
                    .ThenByDescending(item => item.Id)
                    .Take(DanmuDownloadDefaults.MaximumRecords)
                    .ToList();
            }

            WriteAtomic(_recordsFile, new DanmuDownloadRecordEnvelope(_records));
            return appended;
        }
    }

    public void UpdateRecordDanmuCount(long id, int count)
    {
        lock (_sync)
        {
            var index = _records.FindIndex(record => record.Id == id);
            if (index < 0)
            {
                return;
            }

            _records[index] = _records[index] with { DanmuCount = Math.Max(0, count) };
            WriteAtomic(_recordsFile, new DanmuDownloadRecordEnvelope(_records));
        }
    }

    public DownloadRecordDeleteResult DeleteRecords(IReadOnlySet<long> recordIds, bool deleteLocalFiles)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        lock (_sync)
        {
            var selected = _records.Where(record => recordIds.Contains(record.Id)).ToArray();
            if (selected.Length == 0)
            {
                return new DownloadRecordDeleteResult(0, 0, 0, 0, 0, 0);
            }

            var requestedFiles = 0;
            var deletedFiles = 0;
            var missingFiles = 0;
            var failedFiles = 0;
            var retainedSharedFiles = 0;
            if (deleteLocalFiles)
            {
                var retainedPaths = _records
                    .Where(record => !recordIds.Contains(record.Id))
                    .Select(record => record.FilePath.Trim())
                    .Where(path => path.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var selectedPaths = selected
                    .Select(record => record.FilePath.Trim())
                    .Where(path => path.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (var path in selectedPaths)
                {
                    if (retainedPaths.Contains(path))
                    {
                        retainedSharedFiles++;
                        continue;
                    }

                    requestedFiles++;
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                            deletedFiles++;
                        }
                        else
                        {
                            missingFiles++;
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        failedFiles++;
                    }
                }
            }

            var selectedIdSet = selected.Select(record => record.Id).ToHashSet();
            _records = _records.Where(record => !selectedIdSet.Contains(record.Id)).ToList();
            WriteAtomic(_recordsFile, new DanmuDownloadRecordEnvelope(_records));
            return new DownloadRecordDeleteResult(
                selected.Length, requestedFiles, deletedFiles, missingFiles, failedFiles, retainedSharedFiles);
        }
    }

    public int EnqueueTasks(IEnumerable<DanmuDownloadInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        lock (_sync)
        {
            var activeKeys = _tasks
                .Where(task => task.StatusEnum is DownloadQueueStatus.Pending or DownloadQueueStatus.Running)
                .Select(task => $"{task.EpisodeId}|{task.Source}|{task.Format}")
                .ToHashSet(StringComparer.Ordinal);

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var added = 0;
            foreach (var input in inputs)
            {
                ArgumentNullException.ThrowIfNull(input);
                var key = $"{input.EpisodeId}|{input.Source}|{input.Format.Value()}";
                if (!activeKeys.Add(key))
                {
                    continue;
                }

                var taskId = ++_nextTaskId;
                _tasks.Add(new DanmuDownloadTask(
                    taskId, now, now,
                    input.ApiBaseUrl, input.AnimeTitle, input.EpisodeTitle,
                    input.EpisodeId, input.EpisodeNo, input.Source,
                    input.Format.Value(), input.FileNameTemplate, input.ConflictPolicy.Key(),
                    DownloadQueueStatus.Pending.Key(), 0, "等待下载", 0, input.AnimeId));
                added++;
            }

            if (added > 0)
            {
                _tasks = _tasks.TakeLast(DanmuDownloadDefaults.MaximumQueueTasks).ToList();
                WriteAtomic(_queueFile, new DanmuDownloadTaskEnvelope(_tasks));
            }

            return added;
        }
    }

    public bool SetTaskStatus(long taskId, DownloadQueueStatus status, string detail, bool incrementAttempt = false)
    {
        lock (_sync)
        {
            return ReplaceTask(taskId, task => task.With(
                status: status,
                lastDetail: string.IsNullOrWhiteSpace(detail) ? null : detail,
                incrementAttempt: incrementAttempt));
        }
    }

    public bool UpdateTaskInput(long taskId, DanmuDownloadInput input, string detail)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_sync)
        {
            return ReplaceTask(taskId, task => task.With(
                input: input,
                lastDetail: string.IsNullOrWhiteSpace(detail) ? null : detail));
        }
    }

    public bool SetTaskRetryNotBefore(long taskId, long timestampMs)
    {
        lock (_sync)
        {
            return ReplaceTask(taskId, task => task.With(retryNotBeforeAt: Math.Max(0, timestampMs)));
        }
    }

    public int ResetTasks(IReadOnlySet<long> taskIds, string detail)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        lock (_sync)
        {
            var count = 0;
            for (var index = 0; index < _tasks.Count; index++)
            {
                if (!taskIds.Contains(_tasks[index].TaskId))
                {
                    continue;
                }

                count++;
                _tasks[index] = _tasks[index].With(
                    status: DownloadQueueStatus.Pending,
                    lastDetail: detail);
            }

            if (count > 0)
            {
                WriteAtomic(_queueFile, new DanmuDownloadTaskEnvelope(_tasks));
            }

            return count;
        }
    }

    public int MarkRunningTasksAsPending(string detail)
    {
        lock (_sync)
        {
            var count = 0;
            for (var index = 0; index < _tasks.Count; index++)
            {
                if (_tasks[index].StatusEnum != DownloadQueueStatus.Running)
                {
                    continue;
                }

                count++;
                _tasks[index] = _tasks[index].With(
                    status: DownloadQueueStatus.Pending,
                    lastDetail: detail);
            }

            if (count > 0)
            {
                WriteAtomic(_queueFile, new DanmuDownloadTaskEnvelope(_tasks));
            }

            return count;
        }
    }

    public void ClearQueueTasks()
    {
        lock (_sync)
        {
            _tasks = [];
            WriteAtomic(_queueFile, new DanmuDownloadTaskEnvelope(_tasks));
        }
    }

    public int ClearCompletedQueueTasks()
    {
        lock (_sync)
        {
            var kept = _tasks.Where(task => task.StatusEnum is
                DownloadQueueStatus.Pending or
                DownloadQueueStatus.Running or
                DownloadQueueStatus.Failed).ToList();
            var removed = _tasks.Count - kept.Count;
            if (removed > 0)
            {
                _tasks = kept;
                WriteAtomic(_queueFile, new DanmuDownloadTaskEnvelope(_tasks));
            }

            return removed;
        }
    }

    public void ReorderQueueTasks(IReadOnlyList<DanmuDownloadTask> reordered)
    {
        ArgumentNullException.ThrowIfNull(reordered);
        lock (_sync)
        {
            _tasks = reordered.ToList();
            WriteAtomic(_queueFile, new DanmuDownloadTaskEnvelope(_tasks));
        }
    }

    public (DownloadDirectorySyncResult Result, IReadOnlyList<DanmuDownloadRecord> Imported) MergeSyncedRecords(
        IReadOnlyList<DanmuDownloadRecord> imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        lock (_sync)
        {
            var existingPaths = _records
                .Select(record => record.FilePath.Trim())
                .Where(path => path.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var merged = new List<DanmuDownloadRecord>();
            foreach (var record in imported)
            {
                if (record.FilePath.Trim().Length == 0 || !existingPaths.Add(record.FilePath.Trim()))
                {
                    continue;
                }

                merged.Add(record);
            }

            if (merged.Count > 0)
            {
                _records = _records.Concat(merged)
                    .OrderByDescending(record => record.CreatedAt)
                    .ThenByDescending(record => record.Id)
                    .Take(DanmuDownloadDefaults.MaximumRecords)
                    .ToList();
                WriteAtomic(_recordsFile, new DanmuDownloadRecordEnvelope(_records));
            }

            return (new DownloadDirectorySyncResult(0, merged.Count, 0, false), merged);
        }
    }

    private bool ReplaceTask(long taskId, Func<DanmuDownloadTask, DanmuDownloadTask> mutate)
    {
        var index = _tasks.FindIndex(task => task.TaskId == taskId);
        if (index < 0)
        {
            return false;
        }

        _tasks[index] = mutate(_tasks[index]);
        WriteAtomic(_queueFile, new DanmuDownloadTaskEnvelope(_tasks));
        return true;
    }

    private T Load<T>(string file, Func<T> fallback)
    {
        try
        {
            if (!File.Exists(file))
            {
                return fallback();
            }

            using var stream = File.OpenRead(file);
            var value = JsonSerializer.Deserialize(stream, typeof(T), DanmuDownloadJsonContext.Default);
            return value is T typed ? typed : fallback();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            OnPersistFailed($"弹幕下载数据文件读取失败（{Path.GetFileName(file)}）：{error.GetType().Name} {error.Message}；已回退为空数据");
            _startupDiagnostics.Add($"读取 {Path.GetFileName(file)} 失败：{error.GetType().Name} {error.Message}");
            return fallback();
        }
    }

    private void WriteAtomic<T>(string file, T value)
    {
        try
        {
            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = file + ".tmp";
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, value, typeof(T), DanmuDownloadJsonContext.Default);
            }

            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            OnPersistFailed($"弹幕下载数据文件写入失败（{Path.GetFileName(file)}）：{error.Message}；状态仍保留在内存中");
        }
    }

    private void OnPersistFailed(string diagnostic)
    {
        PersistFailed?.Invoke(diagnostic);
    }
}
