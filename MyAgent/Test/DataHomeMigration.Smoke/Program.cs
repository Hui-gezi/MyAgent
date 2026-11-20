// 数据目录迁移冒烟测试：dotnet run --project Test/DataHomeMigration.Smoke
using System.Text.Json;
using MyAgent.App;
using MyAgent.Schedule;
using MyAgent.Sessions;

var root = Path.Combine(Path.GetTempPath(), "myagent-migration-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var failures = 0;

void Check(bool ok, string name)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (!ok) failures++;
}

try
{
    // ---- 场景 1：设置页改目录（记录 migrateFrom）→ 启动迁移，历史会话可见 ----
    var anchor = Path.Combine(root, "s1-anchor");
    var target = Path.Combine(root, "s1-target");
    var oldStore = new SessionStore(anchor, anchor);
    var session = oldStore.CreateSession(Path.Combine(root, "ws"));
    session.Append("user/message", new { text = "历史对话", source = "user" });
    File.WriteAllText(Path.Combine(anchor, "schedules.json"),
        """[{"id":"sch_old","title":"旧任务","prompt":"p","kind":"after","scheduledAt":"2026-01-01T00:00:00+00:00","status":"active","createdAt":"2026-01-01T00:00:00+00:00","updatedAt":"2026-01-01T00:00:00+00:00"}]""");
    Directory.CreateDirectory(Path.Combine(anchor, "logs"));
    File.WriteAllText(Path.Combine(anchor, "logs", "crash-old.log"), "old log");

    var settings = new SettingsService(anchor);
    settings.Update(JsonSerializer.SerializeToElement(new { dataHome = target, dataHomeMigrateFrom = anchor }));

    DataHomeMigration.Run(anchor, target, settings);

    var newStore = new SessionStore(anchor, target);
    Check(newStore.ListSessions().Any(s => s.Id == session.Id), "迁移后新目录能列出历史会话");
    var reopened = newStore.OpenSession(session.Id);
    Check(reopened is not null && reopened.Events.Count > 0, "迁移后会话内容完整");
    Check(File.Exists(Path.Combine(target, "schedules.json")) &&
          new ScheduleStore(target).Get("sch_old") is not null, "定时任务迁移");
    Check(File.Exists(Path.Combine(target, "logs", "crash-old.log")), "日志迁移");
    Check(Directory.Exists(Path.Combine(anchor, "sessions")), "旧目录文件保留");
    Check(settings.Current.DataHomeMigrateFrom == "", "迁移完成后清空待迁移标记");
    Check(File.Exists(Path.Combine(target, DataHomeMigration.MarkerFileName)), "目标目录写入迁移标记");

    // ---- 场景 2：幂等——用户在新目录删除会话后，重启不会"复活" ----
    newStore.DeleteSession(session.Id);
    DataHomeMigration.Run(anchor, target, settings);
    Check(!new SessionStore(anchor, target).ListSessions().Any(s => s.Id == session.Id),
        "已合并来源不重复合并（删除的会话不复活）");

    // ---- 场景 3：旧版本只改配置未迁移的兜底（无 migrateFrom、无标记）----
    var anchor3 = Path.Combine(root, "s3-anchor");
    var target3 = Path.Combine(root, "s3-target");
    var legacySession = new SessionStore(anchor3, anchor3).CreateSession(Path.Combine(root, "ws"));
    var settings3 = new SettingsService(anchor3);
    settings3.Update(JsonSerializer.SerializeToElement(new { dataHome = target3 })); // 旧版本行为：只改 dataHome
    DataHomeMigration.Run(anchor3, target3, settings3);
    Check(new SessionStore(anchor3, target3).ListSessions().Any(s => s.Id == legacySession.Id),
        "旧版本已切换目录：启动兜底把锚点历史数据合并进来");

    // ---- 场景 4：schedules 按 id 合并，目标已有记录优先 ----
    var src4 = Path.Combine(root, "s4-src");
    var dst4 = Path.Combine(root, "s4-dst");
    Directory.CreateDirectory(src4); Directory.CreateDirectory(dst4);
    File.WriteAllText(Path.Combine(src4, "schedules.json"),
        """[{"id":"a","title":"源a","prompt":"p","kind":"after","scheduledAt":"2026-01-01T00:00:00+00:00","status":"active","createdAt":"2026-01-01T00:00:00+00:00","updatedAt":"2026-01-01T00:00:00+00:00"},{"id":"b","title":"源b","prompt":"p","kind":"after","scheduledAt":"2026-01-01T00:00:00+00:00","status":"active","createdAt":"2026-01-01T00:00:00+00:00","updatedAt":"2026-01-01T00:00:00+00:00"}]""");
    File.WriteAllText(Path.Combine(dst4, "schedules.json"),
        """[{"id":"a","title":"目标a","prompt":"p","kind":"after","scheduledAt":"2026-01-01T00:00:00+00:00","status":"active","createdAt":"2026-01-01T00:00:00+00:00","updatedAt":"2026-01-01T00:00:00+00:00"}]""");
    DataHomeMigration.MergeInto(src4, dst4);
    var merged = new ScheduleStore(dst4);
    Check(merged.Get("a")?.Title == "目标a" && merged.Get("b")?.Title == "源b",
        "schedules 合并：目标优先、源补缺");

    // ---- 场景 5：会话文件"新者胜"（改设置到重启之间目标里继续写的内容不丢）----
    var src5 = Path.Combine(root, "s5-src", "sessions", "--ws--", "s_x");
    var dst5 = Path.Combine(root, "s5-dst", "sessions", "--ws--", "s_x");
    Directory.CreateDirectory(src5); Directory.CreateDirectory(dst5);
    var srcFile = Path.Combine(src5, SessionStore.SessionFileName);
    var dstFile = Path.Combine(dst5, SessionStore.SessionFileName);
    File.WriteAllText(srcFile, "old");
    File.WriteAllText(dstFile, "newer");
    File.SetLastWriteTimeUtc(srcFile, DateTime.UtcNow.AddMinutes(-10));
    File.SetLastWriteTimeUtc(dstFile, DateTime.UtcNow);
    DataHomeMigration.MergeInto(Path.Combine(root, "s5-src"), Path.Combine(root, "s5-dst"));
    Check(File.ReadAllText(dstFile) == "newer", "目标文件更新时不被旧文件覆盖");
    File.SetLastWriteTimeUtc(srcFile, DateTime.UtcNow.AddMinutes(10));
    DataHomeMigration.MergeInto(Path.Combine(root, "s5-src"), Path.Combine(root, "s5-dst"));
    Check(File.ReadAllText(dstFile) == "old", "源文件更新时覆盖目标旧文件");

    // ---- 场景 6：恢复默认（target=锚点）时从自定义目录合并回来 ----
    var anchor6 = Path.Combine(root, "s6-anchor");
    var custom6 = Path.Combine(root, "s6-custom");
    var customSession = new SessionStore(anchor6, custom6).CreateSession(Path.Combine(root, "ws"));
    var settings6 = new SettingsService(anchor6);
    settings6.Update(JsonSerializer.SerializeToElement(new { dataHome = custom6 }));
    DataHomeMigration.Run(anchor6, custom6, settings6); // 第一次切换
    settings6.Update(JsonSerializer.SerializeToElement(new { dataHome = "", dataHomeMigrateFrom = custom6 }));
    DataHomeMigration.Run(anchor6, anchor6, settings6); // 恢复默认
    Check(new SessionStore(anchor6, anchor6).ListSessions().Any(s => s.Id == customSession.Id),
        "恢复默认目录时数据合并回锚点");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
