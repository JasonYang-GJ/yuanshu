using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScreenGuide.Core.Memories;
using ScreenGuide.Core.Sessions;

namespace ScreenGuide.Persistence.Sqlite;

public sealed class SqliteSessionStore : ISessionStore
{
    private static readonly string[] FrozenRouteColumns =
    [
        "frozen_route_status",
        "frozen_provider_id",
        "frozen_model_id",
        "frozen_data_destination",
        "frozen_sends_data_off_device",
        "frozen_at_utc",
        "frozen_route_failure_code"
    ];
    private readonly string _connectionString;

    public SqliteSessionStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath.Trim()),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sessions';";
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (count != 1)
        {
            throw new InvalidOperationException("统一会话数据表尚未完成初始化。");
        }

        command.CommandText = "SELECT name FROM pragma_table_info('session_turns');";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(0));
            }
        }

        var missingColumns = FrozenRouteColumns.Where(column => !columns.Contains(column)).ToArray();
        if (missingColumns.Length != 0)
        {
            throw new InvalidOperationException(
                $"统一会话数据库 schema v8 不完整，session_turns 缺少冻结路由列：{string.Join(", ", missingColumns)}。");
        }
    }

    public async Task CreateSessionAsync(
        SessionRecord session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        if (session.IsCurrent)
        {
            await ClearCurrentAsync(connection, transaction, session.UpdatedAtUtc, cancellationToken)
                .ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sessions(
                id, conversation_id, created_by_device_id, title, status, is_current,
                selected_project_id, created_at_utc, updated_at_utc, last_active_at_utc, version)
            VALUES(
                $id, $conversationId, $deviceId, $title, $status, $isCurrent,
                $selectedProjectId, $createdAtUtc, $updatedAtUtc, $lastActiveAtUtc, $version);
            """;
        Add(command, "$id", session.Id);
        Add(command, "$conversationId", session.ConversationId);
        Add(command, "$deviceId", session.CreatedByDeviceId);
        command.Parameters.AddWithValue("$title", session.Title);
        command.Parameters.AddWithValue("$status", session.Status.ToString());
        command.Parameters.AddWithValue("$isCurrent", session.IsCurrent ? 1 : 0);
        command.Parameters.AddWithValue("$selectedProjectId", GuidOrNull(session.SelectedProjectId));
        command.Parameters.AddWithValue("$createdAtUtc", ToDb(session.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedAtUtc", ToDb(session.UpdatedAtUtc));
        command.Parameters.AddWithValue("$lastActiveAtUtc", ToDb(session.LastActiveAtUtc));
        command.Parameters.AddWithValue("$version", session.Version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public Task<SessionRecord?> GetSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        GetSingleSessionAsync("SELECT * FROM sessions WHERE id = $id;", sessionId, cancellationToken);

    public Task<SessionRecord?> GetSessionByConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        GetSingleSessionAsync(
            "SELECT * FROM sessions WHERE conversation_id = $id;",
            conversationId,
            cancellationToken);

    public async Task<SessionRecord?> GetCurrentSessionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM sessions WHERE is_current = 1 LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSession(reader) : null;
    }

    public async Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM sessions ORDER BY is_current DESC, last_active_at_utc DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<SessionRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(ReadSession(reader));
        }

        return values;
    }

    public async Task<SessionRecord> SetCurrentSessionAsync(
        Guid sessionId,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await ClearCurrentAsync(connection, transaction, changedAtUtc, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sessions
            SET is_current = 1, updated_at_utc = $changedAtUtc,
                last_active_at_utc = $changedAtUtc, version = version + 1
            WHERE id = $id AND status = 'Active';
            """;
        Add(command, "$id", sessionId);
        command.Parameters.AddWithValue("$changedAtUtc", ToDb(changedAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("会话不存在或已经归档。");
        }

        transaction.Commit();
        return await GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("会话状态保存失败。");
    }

    public async Task<SessionRecord> SetSelectedProjectAsync(
        Guid sessionId,
        Guid? projectId,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sessions
            SET selected_project_id = $projectId, updated_at_utc = $changedAtUtc,
                last_active_at_utc = $changedAtUtc, version = version + 1
            WHERE id = $id AND status = 'Active';
            """;
        Add(command, "$id", sessionId);
        command.Parameters.AddWithValue("$projectId", GuidOrNull(projectId));
        command.Parameters.AddWithValue("$changedAtUtc", ToDb(changedAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("会话不存在或已经归档。");
        }

        return await GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("会话状态保存失败。");
    }

    public async Task<SessionTurnRegistration> StartTurnAsync(
        Guid sessionId,
        string inputText,
        string inputModality,
        string idempotencyKey,
        SessionTurnFrozenRoute frozenRoute,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputText))
        {
            throw new ArgumentException("会话输入不能为空。", nameof(inputText));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("会话请求必须包含幂等编号。", nameof(idempotencyKey));
        }

        ArgumentNullException.ThrowIfNull(frozenRoute);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var duplicate = await GetTurnByIdempotencyAsync(
                connection,
                transaction,
                sessionId,
                idempotencyKey.Trim(),
                cancellationToken)
            .ConfigureAwait(false);
        if (duplicate is not null)
        {
            transaction.Commit();
            return new SessionTurnRegistration(false, duplicate);
        }

        var sequence = await NextTurnSequenceAsync(connection, transaction, sessionId, cancellationToken)
            .ConfigureAwait(false);
        var turn = new SessionTurnRecord
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            SequenceNumber = sequence,
            InputText = inputText.Trim(),
            InputModality = string.Equals(inputModality, "Voice", StringComparison.OrdinalIgnoreCase)
                ? "Voice"
                : string.Equals(inputModality, "ProgrammingTask", StringComparison.OrdinalIgnoreCase)
                    ? "ProgrammingTask"
                    : "Text",
            IdempotencyKey = idempotencyKey.Trim(),
            FrozenRoute = ValidateFrozenRoute(frozenRoute),
            CreatedAtUtc = startedAtUtc,
            UpdatedAtUtc = startedAtUtc
        };
        await InsertTurnAsync(connection, transaction, turn, cancellationToken).ConfigureAwait(false);
        await TouchSessionAsync(connection, transaction, sessionId, startedAtUtc, cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
        return new SessionTurnRegistration(true, turn);
    }

    public async Task<SessionTurnRecord> UpdateTurnAsync(
        SessionTurnRecord turn,
        long expectedVersion,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE session_turns
            SET work_kind = $workKind, phase = $phase, missing_context = $missingContext,
                intent_kind = $intentKind,
                expected_intent_kind = $expectedIntentKind,
                expected_target = $expectedTarget, plan_target = $planTarget,
                plan_id = $planId,
                conversation_turn_id = $conversationTurnId, task_id = $taskId,
                operation_id = $operationId, project_id = $projectId, file_path = $filePath,
                window_handle = $windowHandle, window_title = $windowTitle,
                window_process_name = $windowProcessName,
                window_process_id = $windowProcessId,
                window_process_started_at_utc = $windowProcessStartedAtUtc,
                requires_confirmation = $requiresConfirmation,
                confirmation_granted = $confirmationGranted,
                cancellation_requested = $cancellationRequested,
                memory_outbound_state = $memoryOutboundState,
                memory_consent_id = $memoryConsentId,
                memory_prepared_at_utc = $memoryPreparedAtUtc,
                memory_expires_at_utc = $memoryExpiresAtUtc,
                memory_consumed_at_utc = $memoryConsumedAtUtc,
                memory_origin_provider_id = $memoryProviderId,
                memory_origin_model_id = $memoryModelId,
                memory_origin_destination = $memoryDestination,
                memory_prompt_id = $memoryPromptId,
                memory_prompt_version = $memoryPromptVersion,
                memory_prompt_hash = $memoryPromptHash,
                memory_project_id = $memoryProjectId,
                memory_item_refs_json = $memoryItemRefsJson,
                memory_item_count = $memoryItemCount,
                memory_total_characters = $memoryTotalCharacters,
                memory_manifest_hash = $memoryManifestHash,
                result_summary = $resultSummary, failure_code = $failureCode,
                failure_message = $failureMessage, updated_at_utc = $changedAtUtc,
                completed_at_utc = $completedAtUtc, version = version + 1
            WHERE id = $id AND session_id = $sessionId AND version = $expectedVersion;
            """;
        Add(command, "$id", turn.Id);
        Add(command, "$sessionId", turn.SessionId);
        command.Parameters.AddWithValue("$workKind", turn.WorkKind.ToString());
        command.Parameters.AddWithValue("$phase", turn.Phase.ToString());
        command.Parameters.AddWithValue("$missingContext", turn.MissingContext.ToString());
        command.Parameters.AddWithValue("$intentKind", TextOrNull(turn.IntentKind));
        command.Parameters.AddWithValue("$expectedIntentKind", TextOrNull(turn.ExpectedIntentKind));
        command.Parameters.AddWithValue("$expectedTarget", TextOrNull(turn.ExpectedTarget));
        command.Parameters.AddWithValue("$planTarget", TextOrNull(turn.PlanTarget));
        command.Parameters.AddWithValue("$planId", GuidOrNull(turn.PlanId));
        command.Parameters.AddWithValue("$conversationTurnId", GuidOrNull(turn.ConversationTurnId));
        command.Parameters.AddWithValue("$taskId", GuidOrNull(turn.TaskId));
        command.Parameters.AddWithValue("$operationId", TextOrNull(turn.OperationId));
        command.Parameters.AddWithValue("$projectId", GuidOrNull(turn.ProjectId));
        command.Parameters.AddWithValue("$filePath", TextOrNull(turn.FilePath));
        command.Parameters.AddWithValue("$windowHandle", (object?)turn.WindowHandle ?? DBNull.Value);
        command.Parameters.AddWithValue("$windowTitle", TextOrNull(turn.WindowTitle));
        command.Parameters.AddWithValue("$windowProcessName", TextOrNull(turn.WindowProcessName));
        command.Parameters.AddWithValue("$windowProcessId", (object?)turn.WindowProcessId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$windowProcessStartedAtUtc",
            DateOrNull(turn.WindowProcessStartTimeUtc));
        command.Parameters.AddWithValue("$requiresConfirmation", turn.RequiresConfirmation ? 1 : 0);
        command.Parameters.AddWithValue("$confirmationGranted", turn.ConfirmationGranted ? 1 : 0);
        command.Parameters.AddWithValue("$cancellationRequested", turn.CancellationRequested ? 1 : 0);
        AddMemoryOutbound(command, turn.MemoryOutboundState, turn.MemoryOutbound);
        command.Parameters.AddWithValue("$resultSummary", TextOrNull(turn.ResultSummary));
        command.Parameters.AddWithValue("$failureCode", TextOrNull(turn.FailureCode));
        command.Parameters.AddWithValue("$failureMessage", TextOrNull(turn.FailureMessage));
        command.Parameters.AddWithValue("$changedAtUtc", ToDb(changedAtUtc));
        command.Parameters.AddWithValue("$completedAtUtc", DateOrNull(turn.CompletedAtUtc));
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("会话状态已经变化，请读取最新状态后重试。");
        }

        await TouchSessionAsync(connection, transaction, turn.SessionId, changedAtUtc, cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
        return await GetTurnAsync(turn.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("会话请求状态保存失败。");
    }

    public Task<SessionTurnRecord?> GetTurnAsync(
        Guid turnId,
        CancellationToken cancellationToken = default) =>
        GetSingleTurnAsync("SELECT * FROM session_turns WHERE id = $id;", turnId, cancellationToken);

    public async Task<IReadOnlyList<SessionTurnRecord>> GetTurnsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM session_turns WHERE session_id = $id ORDER BY sequence_number;";
        Add(command, "$id", sessionId);
        return await ReadTurnsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionTurnPage> GetTurnsPageAsync(
        Guid sessionId,
        int? beforeSequenceNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM session_turns
            WHERE session_id = $id
              AND ($beforeSequenceNumber IS NULL OR sequence_number < $beforeSequenceNumber)
            ORDER BY sequence_number DESC
            LIMIT $limit;
            """;
        Add(command, "$id", sessionId);
        command.Parameters.AddWithValue(
            "$beforeSequenceNumber",
            beforeSequenceNumber is { } value ? value : DBNull.Value);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        var descending = await ReadTurnsAsync(command, cancellationToken).ConfigureAwait(false);
        var hasMore = descending.Count > pageSize;
        var items = descending.Take(pageSize).Reverse().ToArray();
        return new SessionTurnPage(
            items,
            hasMore && items.Length > 0 ? items[0].SequenceNumber : null,
            hasMore);
    }

    public async Task<IReadOnlyList<SessionTurnRecord>> GetActiveTurnsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM session_turns
            WHERE session_id = $id
              AND phase NOT IN ('Completed', 'Failed', 'Cancelled', 'Interrupted')
            ORDER BY sequence_number;
            """;
        Add(command, "$id", sessionId);
        return await ReadTurnsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionTurnRecord>> GetActiveTurnsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM session_turns
            WHERE session_id = $id
              AND phase NOT IN ('Completed', 'Failed', 'Cancelled', 'Interrupted')
            ORDER BY
              CASE WHEN phase IN (
                'Understanding', 'Responding', 'WaitingForProject', 'WaitingForFile',
                'WaitingForWindow', 'WaitingForWindowConsent', 'WaitingForConfirmation',
                'WaitingForMemoryOutboundConsent', 'WaitingForPointerAnswerConsent', 'Executing', 'ObservingWindow'
              ) THEN 0 ELSE 1 END,
              sequence_number DESC,
              updated_at_utc DESC,
              id
            LIMIT $limit;
            """;
        Add(command, "$id", sessionId);
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadTurnsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionRecoveryResult> RecoverInterruptedAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var interrupted = new List<Guid>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT id FROM session_turns
                WHERE phase IN ('Understanding', 'Responding', 'Executing', 'ObservingWindow', 'ProgrammingTask', 'WaitingForUser', 'WaitingForMemoryOutboundConsent', 'WaitingForPointerAnswerConsent')
                   OR (phase = 'WaitingForWindowConsent' AND failure_code IN ('visual_search_consent_required', 'desktop_task_consent_required'));
                """;
            await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                interrupted.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE session_turns
                SET phase = 'Interrupted', missing_context = 'None',
                    failure_code = 'host_restarted',
                    failure_message = '元枢重启时这项工作仍在运行，已安全停止。',
                    memory_outbound_state = CASE
                        WHEN memory_outbound_state IN ('Prepared', 'WaitingForMemoryOutboundConsent', 'Committing')
                        THEN 'Interrupted'
                        ELSE memory_outbound_state
                    END,
                    completed_at_utc = $recoveredAtUtc, updated_at_utc = $recoveredAtUtc,
                    version = version + 1
                WHERE phase IN ('Understanding', 'Responding', 'Executing', 'ObservingWindow', 'ProgrammingTask', 'WaitingForUser', 'WaitingForMemoryOutboundConsent', 'WaitingForPointerAnswerConsent')
                   OR (phase = 'WaitingForWindowConsent' AND failure_code IN ('visual_search_consent_required', 'desktop_task_consent_required'));

                UPDATE session_turns
                SET phase = 'WaitingForWindow', missing_context = 'Window',
                    window_handle = NULL, window_title = NULL, window_process_name = NULL,
                    confirmation_granted = 0, updated_at_utc = $recoveredAtUtc,
                    version = version + 1
                WHERE phase = 'WaitingForWindowConsent';
                """;
            update.Parameters.AddWithValue("$recoveredAtUtc", ToDb(recoveredAtUtc));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        return new SessionRecoveryResult(interrupted);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<SessionRecord?> GetSingleSessionAsync(
        string sql,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSession(reader) : null;
    }

    private async Task<SessionTurnRecord?> GetSingleTurnAsync(
        string sql,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTurn(reader) : null;
    }

    private static async Task<IReadOnlyList<SessionTurnRecord>> ReadTurnsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<SessionTurnRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(ReadTurn(reader));
        }

        return values;
    }

    private static async Task ClearCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sessions
            SET is_current = 0, updated_at_utc = $changedAtUtc, version = version + 1
            WHERE is_current = 1;
            """;
        command.Parameters.AddWithValue("$changedAtUtc", ToDb(changedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TouchSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sessions
            SET updated_at_utc = $changedAtUtc, last_active_at_utc = $changedAtUtc,
                version = version + 1
            WHERE id = $id;
            """;
        Add(command, "$id", sessionId);
        command.Parameters.AddWithValue("$changedAtUtc", ToDb(changedAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("会话不存在。");
        }
    }

    private static async Task<SessionTurnRecord?> GetTurnByIdempotencyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT * FROM session_turns
            WHERE session_id = $sessionId AND idempotency_key = $idempotencyKey;
            """;
        Add(command, "$sessionId", sessionId);
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTurn(reader) : null;
    }

    private static async Task<int> NextTurnSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(sequence_number), 0) + 1
            FROM session_turns WHERE session_id = $sessionId;
            """;
        Add(command, "$sessionId", sessionId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task InsertTurnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionTurnRecord turn,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_turns(
                id, session_id, sequence_number, input_text, input_modality, idempotency_key,
                work_kind, phase, missing_context, intent_kind,
                expected_intent_kind, expected_target, plan_target, plan_id,
                conversation_turn_id, task_id, operation_id, project_id, file_path,
                window_handle, window_title, window_process_name,
                requires_confirmation, confirmation_granted, cancellation_requested,
                result_summary, failure_code, failure_message,
                frozen_route_status, frozen_provider_id, frozen_model_id,
                frozen_data_destination, frozen_sends_data_off_device,
                frozen_at_utc, frozen_route_failure_code,
                memory_outbound_state, memory_consent_id, memory_prepared_at_utc,
                memory_expires_at_utc, memory_consumed_at_utc,
                memory_origin_provider_id, memory_origin_model_id, memory_origin_destination,
                memory_prompt_id, memory_prompt_version, memory_prompt_hash,
                memory_project_id, memory_item_refs_json, memory_item_count,
                memory_total_characters, memory_manifest_hash,
                created_at_utc, updated_at_utc, completed_at_utc, version)
            VALUES(
                $id, $sessionId, $sequenceNumber, $inputText, $inputModality, $idempotencyKey,
                $workKind, $phase, $missingContext, $intentKind,
                $expectedIntentKind, $expectedTarget, $planTarget, $planId,
                NULL, NULL, NULL, NULL, NULL,
                NULL, NULL, NULL,
                0, 0, 0,
                NULL, NULL, NULL,
                $frozenRouteStatus, $frozenProviderId, $frozenModelId,
                $frozenDataDestination, $frozenSendsDataOffDevice,
                $frozenAtUtc, $frozenRouteFailureCode,
                $memoryOutboundState, NULL, NULL,
                NULL, NULL,
                NULL, NULL, NULL,
                NULL, NULL, NULL,
                NULL, NULL, NULL,
                NULL, NULL,
                $createdAtUtc, $updatedAtUtc, NULL, 0);
            """;
        Add(command, "$id", turn.Id);
        Add(command, "$sessionId", turn.SessionId);
        command.Parameters.AddWithValue("$sequenceNumber", turn.SequenceNumber);
        command.Parameters.AddWithValue("$inputText", turn.InputText);
        command.Parameters.AddWithValue("$inputModality", turn.InputModality);
        command.Parameters.AddWithValue("$idempotencyKey", turn.IdempotencyKey);
        command.Parameters.AddWithValue("$workKind", turn.WorkKind.ToString());
        command.Parameters.AddWithValue("$phase", turn.Phase.ToString());
        command.Parameters.AddWithValue("$missingContext", turn.MissingContext.ToString());
        command.Parameters.AddWithValue("$intentKind", TextOrNull(turn.IntentKind));
        command.Parameters.AddWithValue("$expectedIntentKind", TextOrNull(turn.ExpectedIntentKind));
        command.Parameters.AddWithValue("$expectedTarget", TextOrNull(turn.ExpectedTarget));
        command.Parameters.AddWithValue("$planTarget", TextOrNull(turn.PlanTarget));
        command.Parameters.AddWithValue("$planId", GuidOrNull(turn.PlanId));
        command.Parameters.AddWithValue("$frozenRouteStatus", turn.FrozenRoute!.Status.ToString());
        command.Parameters.AddWithValue("$frozenProviderId", TextOrNull(turn.FrozenRoute.ProviderId));
        command.Parameters.AddWithValue("$frozenModelId", TextOrNull(turn.FrozenRoute.ModelId));
        command.Parameters.AddWithValue("$frozenDataDestination", TextOrNull(turn.FrozenRoute.DataDestination));
        command.Parameters.AddWithValue(
            "$frozenSendsDataOffDevice",
            turn.FrozenRoute.SendsDataOffDevice is { } sendsDataOffDevice
                ? sendsDataOffDevice ? 1 : 0
                : DBNull.Value);
        command.Parameters.AddWithValue("$frozenAtUtc", ToDb(turn.FrozenRoute.FrozenAtUtc));
        command.Parameters.AddWithValue("$frozenRouteFailureCode", TextOrNull(turn.FrozenRoute.FailureCode));
        command.Parameters.AddWithValue("$memoryOutboundState", MemoryOutboundConsentState.None.ToString());
        command.Parameters.AddWithValue("$createdAtUtc", ToDb(turn.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedAtUtc", ToDb(turn.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
        SqliteConnectionOpener.OpenAsync(_connectionString, cancellationToken);

    private static SessionRecord ReadSession(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        ConversationId = ReadGuid(reader, "conversation_id"),
        CreatedByDeviceId = ReadGuid(reader, "created_by_device_id"),
        Title = reader.GetString(reader.GetOrdinal("title")),
        Status = Enum.Parse<SessionStatus>(reader.GetString(reader.GetOrdinal("status"))),
        IsCurrent = reader.GetInt32(reader.GetOrdinal("is_current")) == 1,
        SelectedProjectId = ReadNullableGuid(reader, "selected_project_id"),
        CreatedAtUtc = ReadDate(reader, "created_at_utc"),
        UpdatedAtUtc = ReadDate(reader, "updated_at_utc"),
        LastActiveAtUtc = ReadDate(reader, "last_active_at_utc"),
        Version = reader.GetInt64(reader.GetOrdinal("version"))
    };

    private static SessionTurnRecord ReadTurn(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        SessionId = ReadGuid(reader, "session_id"),
        SequenceNumber = reader.GetInt32(reader.GetOrdinal("sequence_number")),
        InputText = reader.GetString(reader.GetOrdinal("input_text")),
        InputModality = reader.GetString(reader.GetOrdinal("input_modality")),
        IdempotencyKey = reader.GetString(reader.GetOrdinal("idempotency_key")),
        FrozenRoute = ReadFrozenRoute(reader),
        WorkKind = Enum.Parse<SessionWorkKind>(reader.GetString(reader.GetOrdinal("work_kind"))),
        Phase = Enum.Parse<SessionTurnPhase>(reader.GetString(reader.GetOrdinal("phase"))),
        MissingContext = Enum.Parse<SessionMissingContext>(reader.GetString(reader.GetOrdinal("missing_context"))),
        IntentKind = ReadNullableString(reader, "intent_kind"),
        ExpectedIntentKind = ReadNullableString(reader, "expected_intent_kind"),
        ExpectedTarget = ReadNullableString(reader, "expected_target"),
        PlanTarget = ReadNullableString(reader, "plan_target"),
        PlanId = ReadNullableGuid(reader, "plan_id"),
        ConversationTurnId = ReadNullableGuid(reader, "conversation_turn_id"),
        TaskId = ReadNullableGuid(reader, "task_id"),
        OperationId = ReadNullableString(reader, "operation_id"),
        ProjectId = ReadNullableGuid(reader, "project_id"),
        FilePath = ReadNullableString(reader, "file_path"),
        WindowHandle = ReadNullableLong(reader, "window_handle"),
        WindowTitle = ReadNullableString(reader, "window_title"),
        WindowProcessName = ReadNullableString(reader, "window_process_name"),
        WindowProcessId = ReadNullableInt(reader, "window_process_id"),
        WindowProcessStartTimeUtc = ReadNullableDate(reader, "window_process_started_at_utc"),
        RequiresConfirmation = reader.GetInt32(reader.GetOrdinal("requires_confirmation")) == 1,
        ConfirmationGranted = reader.GetInt32(reader.GetOrdinal("confirmation_granted")) == 1,
        CancellationRequested = reader.GetInt32(reader.GetOrdinal("cancellation_requested")) == 1,
        MemoryOutboundState = ReadMemoryOutboundState(reader),
        MemoryOutbound = ReadMemoryOutbound(reader),
        ResultSummary = ReadNullableString(reader, "result_summary"),
        FailureCode = ReadNullableString(reader, "failure_code"),
        FailureMessage = ReadNullableString(reader, "failure_message"),
        CreatedAtUtc = ReadDate(reader, "created_at_utc"),
        UpdatedAtUtc = ReadDate(reader, "updated_at_utc"),
        CompletedAtUtc = ReadNullableDate(reader, "completed_at_utc"),
        Version = reader.GetInt64(reader.GetOrdinal("version"))
    };

    private static SessionTurnFrozenRoute? ReadFrozenRoute(SqliteDataReader reader)
    {
        var statusOrdinal = reader.GetOrdinal("frozen_route_status");
        if (reader.IsDBNull(statusOrdinal))
        {
            return null;
        }

        return ValidateFrozenRoute(new SessionTurnFrozenRoute
        {
            Status = Enum.Parse<SessionTurnRouteStatus>(reader.GetString(statusOrdinal)),
            ProviderId = ReadNullableString(reader, "frozen_provider_id"),
            ModelId = ReadNullableString(reader, "frozen_model_id"),
            DataDestination = ReadNullableString(reader, "frozen_data_destination"),
            SendsDataOffDevice = ReadNullableBoolean(reader, "frozen_sends_data_off_device"),
            FrozenAtUtc = ReadDate(reader, "frozen_at_utc"),
            FailureCode = ReadNullableString(reader, "frozen_route_failure_code")
        });
    }

    private static MemoryOutboundConsentState ReadMemoryOutboundState(SqliteDataReader reader)
    {
        var value = ReadNullableString(reader, "memory_outbound_state");
        return value is null
            ? MemoryOutboundConsentState.None
            : Enum.Parse<MemoryOutboundConsentState>(value, ignoreCase: false);
    }

    private static MemoryOutboundAuditMetadata? ReadMemoryOutbound(SqliteDataReader reader)
    {
        var consentId = ReadNullableGuid(reader, "memory_consent_id");
        if (consentId is null)
        {
            return null;
        }

        var referencesJson = ReadNullableString(reader, "memory_item_refs_json")
            ?? throw new InvalidDataException("记忆出站引用元数据缺失。");
        var references = JsonSerializer.Deserialize<MemoryOutboundItemReference[]>(referencesJson)
            ?? throw new InvalidDataException("记忆出站引用元数据无效。");
        return new MemoryOutboundAuditMetadata(
            consentId.Value,
            ReadNullableDate(reader, "memory_prepared_at_utc")
                ?? throw new InvalidDataException("记忆出站准备时间缺失。"),
            ReadNullableDate(reader, "memory_expires_at_utc")
                ?? throw new InvalidDataException("记忆出站到期时间缺失。"),
            ReadNullableDate(reader, "memory_consumed_at_utc"),
            ReadRequiredString(reader, "memory_origin_provider_id"),
            ReadRequiredString(reader, "memory_origin_model_id"),
            ReadRequiredString(reader, "memory_origin_destination"),
            ReadRequiredString(reader, "memory_prompt_id"),
            ReadRequiredString(reader, "memory_prompt_version"),
            ReadRequiredString(reader, "memory_prompt_hash"),
            ReadNullableGuid(reader, "memory_project_id"),
            references,
            reader.GetInt32(reader.GetOrdinal("memory_item_count")),
            reader.GetInt32(reader.GetOrdinal("memory_total_characters")),
            ReadRequiredString(reader, "memory_manifest_hash"));
    }

    private static void AddMemoryOutbound(
        SqliteCommand command,
        MemoryOutboundConsentState state,
        MemoryOutboundAuditMetadata? metadata)
    {
        command.Parameters.AddWithValue("$memoryOutboundState", state.ToString());
        command.Parameters.AddWithValue("$memoryConsentId", GuidOrNull(metadata?.ConsentId));
        command.Parameters.AddWithValue("$memoryPreparedAtUtc", DateOrNull(metadata?.PreparedAtUtc));
        command.Parameters.AddWithValue("$memoryExpiresAtUtc", DateOrNull(metadata?.ExpiresAtUtc));
        command.Parameters.AddWithValue("$memoryConsumedAtUtc", DateOrNull(metadata?.ConsumedAtUtc));
        command.Parameters.AddWithValue("$memoryProviderId", TextOrNull(metadata?.ProviderId));
        command.Parameters.AddWithValue("$memoryModelId", TextOrNull(metadata?.ModelId));
        command.Parameters.AddWithValue("$memoryDestination", TextOrNull(metadata?.DestinationOrigin));
        command.Parameters.AddWithValue("$memoryPromptId", TextOrNull(metadata?.PromptId));
        command.Parameters.AddWithValue("$memoryPromptVersion", TextOrNull(metadata?.PromptVersion));
        command.Parameters.AddWithValue("$memoryPromptHash", TextOrNull(metadata?.PromptContentHash));
        command.Parameters.AddWithValue("$memoryProjectId", GuidOrNull(metadata?.ProjectId));
        command.Parameters.AddWithValue(
            "$memoryItemRefsJson",
            metadata is null ? DBNull.Value : JsonSerializer.Serialize(metadata.Items));
        command.Parameters.AddWithValue("$memoryItemCount", metadata?.ItemCount as object ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$memoryTotalCharacters",
            metadata?.TotalCharacters as object ?? DBNull.Value);
        command.Parameters.AddWithValue("$memoryManifestHash", TextOrNull(metadata?.ManifestHash));
    }

    private static string ReadRequiredString(SqliteDataReader reader, string name) =>
        ReadNullableString(reader, name)
        ?? throw new InvalidDataException($"记忆出站元数据缺少 {name}。");

    private static SessionTurnFrozenRoute ValidateFrozenRoute(SessionTurnFrozenRoute route)
    {
        if (route.FrozenAtUtc == default)
        {
            throw new InvalidOperationException("冻结路由必须记录冻结时间。");
        }

        if (route.Status == SessionTurnRouteStatus.Ready)
        {
            if (string.IsNullOrWhiteSpace(route.ProviderId)
                || string.IsNullOrWhiteSpace(route.ModelId)
                || string.IsNullOrWhiteSpace(route.DataDestination)
                || route.SendsDataOffDevice is null
                || !string.IsNullOrWhiteSpace(route.FailureCode))
            {
                throw new InvalidOperationException("Ready 冻结路由缺少 Provider、模型或数据目的地元数据。");
            }
        }
        else if (route.Status != SessionTurnRouteStatus.Unavailable)
        {
            throw new InvalidOperationException("冻结路由状态无效。");
        }
        else if (string.IsNullOrWhiteSpace(route.FailureCode)
                 || route.DataDestination is not null
                 || route.SendsDataOffDevice is not null)
        {
            throw new InvalidOperationException("Unavailable 冻结路由必须包含失败码且不能声明数据目的地。");
        }

        return route with
        {
            ProviderId = NullIfWhiteSpace(route.ProviderId),
            ModelId = NullIfWhiteSpace(route.ModelId),
            DataDestination = NullIfWhiteSpace(route.DataDestination),
            FrozenAtUtc = route.FrozenAtUtc.ToUniversalTime(),
            FailureCode = NullIfWhiteSpace(route.FailureCode)
        };
    }

    private static void Add(SqliteCommand command, string name, Guid value) =>
        command.Parameters.AddWithValue(name, value.ToString("D"));

    private static object GuidOrNull(Guid? value) => value is null ? DBNull.Value : value.Value.ToString("D");
    private static object TextOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    private static object DateOrNull(DateTimeOffset? value) => value is null ? DBNull.Value : ToDb(value.Value);
    private static Guid ReadGuid(SqliteDataReader reader, string name) => Guid.Parse(reader.GetString(reader.GetOrdinal(name)));
    private static Guid? ReadNullableGuid(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : Guid.Parse(reader.GetString(reader.GetOrdinal(name)));
    private static long? ReadNullableLong(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt64(reader.GetOrdinal(name));
    private static int? ReadNullableInt(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt32(reader.GetOrdinal(name));
    private static bool? ReadNullableBoolean(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt32(reader.GetOrdinal(name)) == 1;
    private static string? ReadNullableString(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string ToDb(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static DateTimeOffset ReadDate(SqliteDataReader reader, string name) => DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal(name)), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : ReadDate(reader, name);
}
