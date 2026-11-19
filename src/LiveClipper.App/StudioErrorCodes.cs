/// <summary>
/// /api/studio/clips/{id} 编辑接口（PUT editor、commands、sessions、undo、redo）非 2xx 响应体顶层的机器可读 code。
/// 值是稳定的 snake_case，客户端可以直接按它分支；这里只新增字段，状态码、原有 error / error.code（kebab-case）/ revision 一律不变。
/// </summary>
internal static class StudioErrorCodes
{
    public const string RevisionConflict = "revision_conflict";
    public const string InvalidProject = "invalid_project";
    public const string InvalidCommand = "invalid_command";
    public const string MissingMedia = "missing_media";
    public const string SessionNotFound = "session_not_found";
    public const string NothingToUndo = "nothing_to_undo";
    public const string NothingToRedo = "nothing_to_redo";
    public const string NotFound = "not_found";
    // R51-Y2b：写路由的 revision / epoch 前置条件与读盘 codec。
    /// <summary>428：PUT editor / commands / undo / redo 缺少 X-Base-Revision（commands 也可用 body.baseRevision）。</summary>
    public const string PreconditionRequired = "precondition_required";
    /// <summary>400：X-Base-Revision 不是非负十进制整数，或与 body.baseRevision 不一致。</summary>
    public const string InvalidRevision = "invalid_revision";
    /// <summary>409：请求带了 X-Editor-Epoch 但与当前服务端 epoch 不同（服务端已重启，history/会话已失效）。</summary>
    public const string StaleEpoch = "stale_epoch";
    /// <summary>409：存储的工程或备份 schemaVersion 比本程序支持的新，fail closed。</summary>
    public const string ProjectSchemaTooNew = "project_schema_too_new";
    /// <summary>422：存储的工程 schemaVersion 字段非法或 JSON 根不是对象。</summary>
    public const string ProjectSchemaInvalid = "project_schema_invalid";
    /// <summary>422：工程备份为空或无法解析。</summary>
    public const string BackupInvalid = "backup_invalid";
    /// <summary>409：数据库 schema_migrations 版本比本程序支持的新。</summary>
    public const string StorageSchemaTooNew = "storage_schema_too_new";

    /// <summary>
    /// EditorRevisionConflictException.Code（EditorRevisionStore 里的 kebab-case）→ 顶层 code。
    /// "session-mismatch"：sessionId 不匹配或已过期 → session_not_found；
    /// "session-busy" 与未知值：别的会话占着工程，属于并发冲突 → revision_conflict。
    /// </summary>
    public static string ForConflict(string conflictCode) => conflictCode switch
    {
        "session-mismatch" => SessionNotFound,
        _ => RevisionConflict,
    };
}
