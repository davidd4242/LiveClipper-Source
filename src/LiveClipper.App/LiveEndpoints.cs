using System.Text.Json;
using LiveClipper.Core;
using LiveClipper.Domain;
using LiveClipper.Live;
using Microsoft.AspNetCore.Http.Features;

namespace LiveClipper.App;

/// <summary>新直播底座的最小可观测/控制端点；旧录制 API 暂时保留，避免破坏现有页面。</summary>
public static class LiveEndpoints
{
    public sealed record LiveRecordingRequest(bool Enabled);
    internal static async Task<IResult> ResolveUidAsync(long uid,Func<long,CancellationToken,Task<ResolvedLiveUser>> resolve,CancellationToken token)
    {
        if(uid<=0)return Results.BadRequest(new {error="请输入大于零的 B 站 UID。"});
        try{return Results.Ok(await resolve(uid,token));}
        catch(ArgumentException ex){return Results.NotFound(new {error=ex.Message});}
        catch(Exception ex) when(ex is HttpRequestException || (ex is TaskCanceledException && !token.IsCancellationRequested))
        {return Results.Json(new {error=ex.Message},statusCode:502);}
    }

    public static void Map(
        WebApplication app,
        LiveRoomManager manager,
        LiveStorageJanitor? storage = null,
        JsonLiveRoomRegistry? registry = null,
        BilibiliPreviewService? preview = null,
        BilibiliRoomProbe? lookup = null,
        ILiveSessionStore? sessions = null,
        LiveRoomRecoveryService? recovery = null,
        LiveDiagnosticsService? diagnostics = null,
        Store? store = null,
        LiveCoordinator? coordinator = null,
        Func<bool>? canRecord = null)
    {
        // 历史会话只读查询：优先用显式传入的存储，否则复用清理器持有的实例，
        // 这样 Program.cs 不需要为新增端点再构造一份存储。
        var sessionStore = sessions ?? storage?.Store;
        if (sessionStore is not null)
        {
            app.MapGet("/api/live/sessions", async (long? roomId, int? limit, int? offset, CancellationToken token) =>
            {
                var (items, total) = await LiveSessionQueries
                    .ListSessionsAsync(sessionStore, roomId, limit, offset, token)
                    .ConfigureAwait(false);
                return Results.Ok(new
                {
                    sessions = items,
                    total,
                    limit = LiveSessionQueries.ClampLimit(limit),
                    offset = LiveSessionQueries.ClampOffset(offset)
                });
            });

            app.MapGet("/api/live/sessions/{id}/segments", async (string id, CancellationToken token) =>
            {
                var detail = await LiveSessionQueries
                    .GetSessionAsync(sessionStore, id, token)
                    .ConfigureAwait(false);
                return detail is null
                    ? Results.NotFound(new { error = "直播会话不存在" })
                    : Results.Ok(new { session = detail.Session, segments = detail.Segments });
            });

            // 一场会话的弹幕（只读）；limit 取最后 N 条，默认 200，上限 2000。
            app.MapGet("/api/live/sessions/{id}/danmaku", async (string id, int? limit, CancellationToken token) =>
            {
                var events = await LiveSessionQueries
                    .ListDanmakuAsync(sessionStore, id, limit, token)
                    .ConfigureAwait(false);
                return events is null
                    ? Results.NotFound(new { error = "直播会话不存在" })
                    : Results.Ok(new
                    {
                        sessionId = id,
                        events,
                        limit = Math.Clamp(
                            limit ?? LiveSessionQueries.DefaultDanmakuLimit,
                            1,
                            LiveSessionQueries.MaxDanmakuLimit)
                    });
            });
        }

        if (lookup is not null)
        {
            app.MapGet("/api/live/resolve", async (string? input, CancellationToken token) =>
            {
                if (string.IsNullOrWhiteSpace(input)
                    || !long.TryParse(input.Trim(), out var value)
                    || value <= 0)
                {
                    return Results.BadRequest(new { error = "请输入正整数直播间号或 B 站 UID" });
                }

                try
                {
                    var room = await lookup.ProbeAsync(value, token).ConfigureAwait(false);
                    return Results.Ok(new
                    {
                        input = value,
                        roomId = room.CanonicalRoomId ?? room.RoomId,
                        resolvedFrom = "room",
                        room.Title,
                        room.Streamer,
                        availability = room.Availability.ToString()
                    });
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("60004", StringComparison.Ordinal))
                {
                    try
                    {
                        var roomId = await lookup.ResolveRoomIdByUidAsync(value, token).ConfigureAwait(false);
                        if (roomId <= 0)
                            return Results.NotFound(new { error = "没有找到该 UID 对应的直播间，请检查 UID 或直接填写房间号" });

                        var room = await lookup.ProbeAsync(roomId, token).ConfigureAwait(false);
                        return Results.Ok(new
                        {
                            input = value,
                            roomId = room.CanonicalRoomId ?? room.RoomId,
                            resolvedFrom = "uid",
                            room.Title,
                            room.Streamer,
                            availability = room.Availability.ToString()
                        });
                    }
                    catch (Exception lookupError) when (lookupError is HttpRequestException or TaskCanceledException)
                    {
                        return Results.Json(new { error = $"B 站 UID 解析失败：{lookupError.Message}" }, statusCode: 502);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    return Results.Json(new { error = $"B 站房间探测失败：{ex.Message}" }, statusCode: 502);
                }
            });
        }

        app.MapGet("/api/live/rooms", async (CancellationToken token) => Results.Ok(new
        {
            rooms = manager.Snapshots(),
            registrations = registry is null
                ? Array.Empty<LiveRoomRegistration>()
                : await registry.ListAsync(token),
            recovery = recovery is null
                ? Array.Empty<LiveRecoveryState>()
                : recovery.Snapshot(),
            maxRooms = manager.MaxRooms
        }));

        if (storage is not null)
        {
            app.MapGet("/api/live/storage", async (CancellationToken token) =>
                Results.Ok(await storage.InspectAsync(token)));
            app.MapPost("/api/live/storage/cleanup", async (CancellationToken token) =>
                Results.Ok(await storage.EnforceAsync(token)));
        }

        if (diagnostics is not null)
        {
            // G4：坏清单/坏登记簿/孤儿目录/恢复状态一次返回；报告本身失败也不影响其他端点。
            app.MapGet("/api/live/diagnostics", async (CancellationToken token) =>
            {
                try
                {
                    return Results.Ok(await diagnostics.InspectAsync(token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return Results.StatusCode(499); // 客户端已断开，按 nginx 的 499 语义安静结束。
                }
                catch (Exception ex)
                {
                    // 失败详情只进服务端日志：错误消息可能带本机绝对路径，响应里不回显。
                    Console.Error.WriteLine($"live diagnostics failed: {ex}");
                    return Results.Json(
                        new { error = "诊断采集失败，详细原因见服务端日志" },
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            });
        }

        app.MapGet("/api/live/rooms/{roomId:long}", (long roomId) =>
        {
            var status = manager.Snapshots().FirstOrDefault(item => item.RoomId == roomId);
            return status is null
                ? Results.NotFound(new { error = "该直播间没有被新直播底座监控" })
                : Results.Ok(status);
        });

        // A4 监控屏专用：旧管线弹幕的轻量 SSE 流。只读 Store 里已有的弹幕缓冲，
        // 不新增写入路径，也不改变 /api/status、/api/danmaku 等旧 UI 依赖的行为。
        // 事件：hello（房间/会话切换时发送一次，带当前会话与监控状态）与 danmaku（逐条弹幕）。
        if (store is not null && coordinator is not null)
        {
            app.MapGet("/api/live/danmaku/stream", async (long roomId, HttpContext http, CancellationToken token) =>
            {
                if (roomId <= 0)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await http.Response.WriteAsJsonAsync(new { error = "直播间号必须是正整数" }, token);
                    return;
                }

                http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                http.Response.StatusCode = StatusCodes.Status200OK;
                http.Response.ContentType = "text/event-stream; charset=utf-8";
                http.Response.Headers.CacheControl = "no-cache, no-store";
                http.Response.Headers["X-Accel-Buffering"] = "no";
                await http.Response.Body.FlushAsync(token).ConfigureAwait(false);

                var seen = new HashSet<string>(StringComparer.Ordinal);
                string? knownSession = null;
                var announced = false;
                var idleTicks = 0;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var room = coordinator.Rooms.FirstOrDefault(item => item.RoomId == roomId);
                        var sessionId = room?.SessionId;
                        if (!announced || !string.Equals(knownSession, sessionId, StringComparison.Ordinal))
                        {
                            knownSession = sessionId;
                            announced = true;
                            seen.Clear();
                            await WriteEventAsync(http, "hello", new
                            {
                                roomId,
                                sessionId,
                                monitoring = room?.Monitoring ?? false,
                                title = room?.Title ?? string.Empty,
                                streamer = room?.Streamer ?? string.Empty
                            }, token).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(sessionId))
                            {
                                // 连上先回填最近 60 条，弹幕窗口不必从空白开始。
                                foreach (var item in store.RecentDanmaku(sessionId, 60).Reverse())
                                {
                                    seen.Add(item.Id);
                                    await WriteDanmakuAsync(http, item, token).ConfigureAwait(false);
                                }
                            }
                            idleTicks = 0;
                        }
                        else if (!string.IsNullOrWhiteSpace(sessionId))
                        {
                            var fresh = store.RecentDanmaku(sessionId, 300)
                                .Where(item => !seen.Contains(item.Id))
                                .Reverse()
                                .ToArray();
                            foreach (var item in fresh)
                            {
                                seen.Add(item.Id);
                                await WriteDanmakuAsync(http, item, token).ConfigureAwait(false);
                            }
                            if (fresh.Length > 0) idleTicks = 0;
                            if (seen.Count > 4000)
                            {
                                // 长场次监控时裁剪去重集合，避免内存随弹幕数无限增长。
                                seen.IntersectWith(store.RecentDanmaku(sessionId, 300).Select(item => item.Id));
                            }
                        }

                        idleTicks++;
                        if (idleTicks >= 20)
                        {
                            idleTicks = 0;
                            await http.Response.WriteAsync(": keep-alive\n\n", token).ConfigureAwait(false);
                            await http.Response.Body.FlushAsync(token).ConfigureAwait(false);
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // 浏览器切换房间或关闭监控屏时正常断开。
                }
            });
        }

        if (preview is not null)
        {
            app.MapGet("/api/live/rooms/{roomId:long}/preview.flv", async (
                long roomId,
                HttpContext http,
                CancellationToken token) =>
            {
                if (roomId <= 0)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await http.Response.WriteAsJsonAsync(new { error = "直播间号必须是正整数" }, token);
                    return;
                }

                try
                {
                    await using var stream = await preview.OpenAsync(roomId, token).ConfigureAwait(false);
                    if (stream is null)
                    {
                        http.Response.StatusCode = StatusCodes.Status409Conflict;
                        await http.Response.WriteAsJsonAsync(new { error = "房间当前不在直播，暂时没有预览流" }, token);
                        return;
                    }

                    http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                    http.Response.StatusCode = StatusCodes.Status200OK;
                    http.Response.ContentType = stream.ContentType;
                    http.Response.Headers.CacheControl = "no-store, no-cache";
                    await stream.Content.CopyToAsync(http.Response.Body, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // 浏览器切走预览时正常断开。
                }
                catch (ArgumentException ex)
                {
                    if (!http.Response.HasStarted)
                    {
                        http.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await http.Response.WriteAsJsonAsync(new { error = ex.Message }, token);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    if (!http.Response.HasStarted)
                    {
                        http.Response.StatusCode = StatusCodes.Status502BadGateway;
                        await http.Response.WriteAsJsonAsync(new { error = $"直播预览取流失败：{ex.Message}" }, token);
                    }
                    else
                    {
                        Console.Error.WriteLine($"live preview room {roomId} interrupted: {ex.Message}");
                        http.Abort();
                    }
                }
            });
        }

        app.MapPost("/api/live/rooms/{roomId:long}/start", async (long roomId, bool? autoRecord, CancellationToken token) =>
        {
            try
            {
                var enabled = autoRecord ?? false;
                if(enabled && canRecord?.Invoke() != true)return Results.Conflict(new {error="请先设置独立录制目录"});
                if(coordinator?.Rooms.Any(room => room.RoomId == roomId) == true)return Results.Conflict(new {error="该房间已由主监控管线管理，请使用主监控录播开关"});
                var status = await manager.StartAsync(roomId, token, enabled).ConfigureAwait(false);
                if (registry is not null)
                {
                    try { await registry.SetActiveAsync(roomId, true, token, status.AutoRecord).ConfigureAwait(false); }
                    catch (Exception ex) { Console.Error.WriteLine($"live room {roomId} registry save failed: {ex.Message}"); }
                }
                if (recovery is not null) await recovery.ResetAsync(roomId, token).ConfigureAwait(false);
                return Results.Ok(status);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        app.MapPost("/api/live/rooms/{roomId:long}/recording", async (long roomId, LiveRecordingRequest body, CancellationToken token) =>
        {
            if(body.Enabled && canRecord?.Invoke() != true)return Results.Conflict(new {error="请先设置独立录制目录"});
            try
            {
                if (!manager.Contains(roomId)) return Results.Conflict(new { error = "房间尚未开始监控" });
                // Persist off before stopping so a restart cannot re-enable recording.
                if(registry is not null && !body.Enabled)await registry.SetActiveAsync(roomId,true,token,false);
                var status=await manager.SetRecordingAsync(roomId,body.Enabled,token);
                if(registry is not null && body.Enabled)await registry.SetActiveAsync(roomId,true,token,true);
                return Results.Ok(status);
            }
            catch(InvalidOperationException ex){return Results.Conflict(new {error=ex.Message});}
        });

        app.MapPost("/api/live/rooms/{roomId:long}/stop", async (long roomId, CancellationToken token) =>
        {
            // G9：Stop 幂等。重复点击/并发调用返回 200 + stopped=false，不再产生 404 伪错误；
            // 已登记但未运行的房间也会被置为非活动，避免"停了却还在自动恢复列表里"。
            var stopped = await manager.StopAsync(roomId).ConfigureAwait(false);
            var deactivated = false;
            if (registry is not null)
            {
                try
                {
                    var registrations = await registry.ListAsync(token).ConfigureAwait(false);
                    if (registrations.Any(item => item.RoomId == roomId))
                    {
                        await registry.SetActiveAsync(roomId, false, token).ConfigureAwait(false);
                        deactivated = true;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"live room {roomId} registry save failed: {ex.Message}");
                }
            }
            if (recovery is not null) await recovery.ResetAsync(roomId, token).ConfigureAwait(false);
            return Results.Ok(new { stopped, alreadyStopped = !stopped, deactivated, roomId });
        });

        app.MapDelete("/api/live/rooms/{roomId:long}", async (long roomId, CancellationToken token) =>
        {
            var stopped = await manager.StopAsync(roomId).ConfigureAwait(false);
            var removed = registry is not null && await registry.RemoveAsync(roomId, token).ConfigureAwait(false);
            if (recovery is not null) await recovery.ResetAsync(roomId, token).ConfigureAwait(false);
            if (!stopped && !removed)
                return Results.NotFound(new { error = "该直播间没有登记或运行记录" });
            return Results.Ok(new { removed = true, stopped, roomId });
        });
    }

    /// <summary>向 SSE 响应写一个命名事件；data 与其余 API 一致使用 camelCase JSON。</summary>
    private static async Task WriteEventAsync(HttpContext http, string name, object payload, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        await http.Response.WriteAsync($"event: {name}\ndata: {json}\n\n", token).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(token).ConfigureAwait(false);
    }

    private static Task WriteDanmakuAsync(HttpContext http, DanmakuEvent item, CancellationToken token)
    {
        return WriteEventAsync(http, "danmaku", new
        {
            item.Id,
            item.SessionId,
            item.SessionTime,
            item.Timestamp,
            item.Type,
            item.Uid,
            item.UserName,
            item.Text,
            item.Weight
        }, token);
    }
}
