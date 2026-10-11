using System.Collections.ObjectModel;
using System.Text.Json;

namespace AliceVision.Core.Agent;

/// <summary>A target is selected and measured by the local AR scene, never by the model.</summary>
public sealed record CompanionTarget(string Id, string Label, double DistanceMeters);

/// <summary>Immutable request-time capability snapshot. A fresh ID is required for every request.</summary>
public sealed class SceneSnapshot
{
    public string SnapshotId { get; }
    public long ZoneRevision { get; }
    public bool TrackingNormal { get; }
    public bool CharacterPlaced { get; }
    public IReadOnlyList<string> AvailableActions { get; }
    public IReadOnlyList<CompanionTarget> Targets { get; }

    public SceneSnapshot(string snapshotId, long zoneRevision, bool trackingNormal, bool characterPlaced,
        IEnumerable<string> availableActions, IEnumerable<CompanionTarget> targets)
    {
        if (!CompanionProtocol.IsIdentifier(snapshotId, 96))
            throw new ArgumentException("无效的空间快照标识。", nameof(snapshotId));
        if (zoneRevision < 0) throw new ArgumentOutOfRangeException(nameof(zoneRevision));
        ArgumentNullException.ThrowIfNull(availableActions);
        ArgumentNullException.ThrowIfNull(targets);
        // Bounded enumeration also prevents an accidental unbounded producer from freezing the app.
        string[] actions = availableActions.Take(13).ToArray();
        CompanionTarget[] targetCopies = targets.Take(33).ToArray();
        if (actions.Length is < 1 or > 12 || actions.Distinct(StringComparer.Ordinal).Count() != actions.Length ||
            actions.Any(action => !CompanionProtocol.IsKnownAction(action)) || !actions.Contains("idle", StringComparer.Ordinal))
            throw new ArgumentException("动作能力白名单无效。", nameof(availableActions));
        if (targetCopies.Length > 32 || targetCopies.Any(target => target is null ||
            !CompanionProtocol.IsIdentifier(target.Id, 64) || !CompanionProtocol.IsSafeText(target.Label, 80) ||
            !double.IsFinite(target.DistanceMeters) || target.DistanceMeters is < 0 or > 10) ||
            targetCopies.Select(target => target.Id).Distinct(StringComparer.Ordinal).Count() != targetCopies.Length)
            throw new ArgumentException("本地目标列表无效。", nameof(targets));
        SnapshotId = snapshotId;
        ZoneRevision = zoneRevision;
        TrackingNormal = trackingNormal;
        CharacterPlaced = characterPlaced;
        AvailableActions = new ReadOnlyCollection<string>(actions);
        Targets = new ReadOnlyCollection<CompanionTarget>(targetCopies);
    }
}

public sealed record CompanionRequest(SceneSnapshot Snapshot, string UserRequest, string? JpegDataUri,
    bool ImageConsent, bool Autonomous = false, string? Persona = null);

public sealed record CompanionDirective(string SnapshotId, long ZoneRevision, string Say, string Action,
    string? TargetId, double Confidence);

public sealed record CompanionApiResult(CompanionDirective? Directive, string? Error)
{
    public bool Success => Directive is not null && Error is null;
    public static CompanionApiResult Rejected(string error) => new(null, error);
}

/// <summary>Model text is untrusted data, not commands or coordinates.</summary>
public static class CompanionProtocol
{
    public const double MinimumMoveConfidence = 0.65;
    public const int MaximumDirectiveCharacters = 4096;
    private static readonly HashSet<string> KnownActions = new(StringComparer.Ordinal)
        { "idle", "look_at_user", "wave", "wave_left", "wave_right", "greet", "welcome", "nod", "bow", "dance", "move_to_target" };
    private static readonly HashSet<string> ResponseKeys = new(StringComparer.Ordinal)
        { "schema_version", "snapshot_id", "say", "action", "target_id", "confidence" };

    public static bool IsKnownAction(string? action) => action is not null && KnownActions.Contains(action);

    public static bool IsIdentifier(string? value, int maximumLength) => value is not null &&
        value.Length > 0 && value.Length <= maximumLength && value.All(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.' or ':');

    public static bool IsSafeText(string? value, int maximumLength) => value is not null &&
        value.Length <= maximumLength && !value.Any(character => char.IsControl(character) && character is not '\n' and not '\r' and not '\t');

    public static CompanionApiResult ParseDirective(string? text, SceneSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.TrackingNormal || !snapshot.CharacterPlaced)
            return CompanionApiResult.Rejected("空间跟踪不稳定或角色尚未放置。");
        if (text is null || text.Length > MaximumDirectiveCharacters)
            return CompanionApiResult.Rejected("AI 指令为空或过长。");
        text = text.Trim();
        // Accept one whole JSON fence only. Never fish an object out of prose or several responses.
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstLine = text.IndexOf('\n');
            if (firstLine < 0 || !text.EndsWith("```", StringComparison.Ordinal))
                return CompanionApiResult.Rejected("AI 指令不是单一 JSON 对象。");
            string language = text[3..firstLine].Trim();
            if (language.Length != 0 && !language.Equals("json", StringComparison.OrdinalIgnoreCase))
                return CompanionApiResult.Rejected("AI 指令格式不受支持。");
            text = text[(firstLine + 1)..^3].Trim();
            if (text.Contains("```", StringComparison.Ordinal))
                return CompanionApiResult.Rejected("AI 指令包含多个代码块。");
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return BadSchema();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
                if (!ResponseKeys.Contains(property.Name) || !seen.Add(property.Name)) return BadSchema();
            if (!root.TryGetProperty("schema_version", out JsonElement schema) || !schema.TryGetInt32(out int version) || version != 1 ||
                !root.TryGetProperty("snapshot_id", out JsonElement snapshotValue) || snapshotValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("action", out JsonElement actionValue) || actionValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("target_id", out JsonElement targetValue) || targetValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
                !root.TryGetProperty("confidence", out JsonElement confidenceValue) || !confidenceValue.TryGetDouble(out double confidence))
                return BadSchema();
            string? snapshotId = snapshotValue.GetString(), action = actionValue.GetString();
            string? targetId = targetValue.ValueKind == JsonValueKind.Null ? null : targetValue.GetString();
            string say = "";
            if (root.TryGetProperty("say", out JsonElement sayValue))
            {
                if (sayValue.ValueKind != JsonValueKind.String) return BadSchema();
                say = sayValue.GetString()!;
            }
            if (!string.Equals(snapshotId, snapshot.SnapshotId, StringComparison.Ordinal))
                return CompanionApiResult.Rejected("AI 返回的空间快照已失效。");
            if (!IsKnownAction(action) || !snapshot.AvailableActions.Contains(action!, StringComparer.Ordinal))
                return CompanionApiResult.Rejected("AI 请求了角色不支持的动作。");
            if (!double.IsFinite(confidence) || confidence is < 0 or > 1 || !IsSafeText(say, 280)) return BadSchema();
            if (action == "move_to_target")
            {
                if (snapshot.ZoneRevision == 0 || confidence < MinimumMoveConfidence ||
                    targetId is null || !snapshot.Targets.Any(target => target.Id.Equals(targetId, StringComparison.Ordinal)))
                    return CompanionApiResult.Rejected("移动置信度不足或目标不在当前活动区白名单中。");
            }
            else if (targetId is not null) return CompanionApiResult.Rejected("非移动动作不能指定移动目标。");
            return new CompanionApiResult(new CompanionDirective(snapshot.SnapshotId, snapshot.ZoneRevision, say, action!, targetId, confidence), null);
        }
        catch (JsonException) { return BadSchema(); }
        catch (InvalidOperationException) { return BadSchema(); }
        catch (FormatException) { return BadSchema(); }
    }

    /// <summary>Must be called on the current AR snapshot immediately before performing an action.</summary>
    public static bool CanExecute(CompanionDirective directive, SceneSnapshot currentSnapshot)
    {
        ArgumentNullException.ThrowIfNull(directive);
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        if (!currentSnapshot.TrackingNormal || !currentSnapshot.CharacterPlaced ||
            !string.Equals(directive.SnapshotId, currentSnapshot.SnapshotId, StringComparison.Ordinal) ||
            directive.ZoneRevision != currentSnapshot.ZoneRevision ||
            !currentSnapshot.AvailableActions.Contains(directive.Action, StringComparer.Ordinal) ||
            !double.IsFinite(directive.Confidence) || directive.Confidence is < 0 or > 1 || !IsSafeText(directive.Say, 280)) return false;
        if (directive.Action != "move_to_target") return directive.TargetId is null;
        return currentSnapshot.ZoneRevision > 0 && directive.Confidence >= MinimumMoveConfidence &&
            directive.TargetId is not null && currentSnapshot.Targets.Any(target => target.Id == directive.TargetId);
    }

    private static CompanionApiResult BadSchema() => CompanionApiResult.Rejected("AI 指令未通过安全格式校验。");
}
