using System.Collections.Concurrent;
using WebRagApi.Models;

namespace WebRagApi.Services;

/// <summary>
/// 文档导入任务的内存存储：任务对象在后台导入过程中被实时更新，
/// 供 GET /api/knowledge/tasks/{id} 查询进度。
/// </summary>
public class IngestionTaskManager
{
    private readonly ConcurrentDictionary<string, IngestionTask> _tasks = new();

    /// <summary>注册一个新任务（由上传/启动扫描触发）</summary>
    public IngestionTask CreateTask(string trigger, IReadOnlyList<IngestionFileProgress> files)
    {
        var task = new IngestionTask
        {
            Trigger = trigger,
            TotalFiles = files.Count,
            StartedAt = DateTimeOffset.Now,
        };
        foreach (var f in files)
            f.Task = task;
        task.Files.AddRange(files);
        _tasks[task.Id] = task;
        return task;
    }

    /// <summary>按 ID 查询任务</summary>
    public IngestionTask? Get(string id) => _tasks.TryGetValue(id, out var task) ? task : null;

    /// <summary>列出全部任务（新任务在前，页面刷新后也能看到历史导入进度）</summary>
    public List<IngestionTask> List() =>
        [.. _tasks.Values.OrderByDescending(t => t.StartedAt).Take(50)];
}
