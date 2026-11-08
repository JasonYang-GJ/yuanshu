using System.Text.Json;

namespace ScreenGuide.Skills.Abstractions;

public enum ApplicationSearchFollowUp { None, OpenFirst, PlayFirst }

/// <summary>One explicit, bounded user request. This value never grants permission by itself.</summary>
public sealed record ApplicationSearchWorkflow(
    string ApplicationId,
    string Query,
    ApplicationSearchFollowUp FollowUp = ApplicationSearchFollowUp.None,
    string? LatestMusicArtist = null,
    string? MusicChart = null)
{
    public const string NetEaseHotChart = "NetEaseHotChart";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApplicationId) || ApplicationId.Length > 256
            || ApplicationId.Any(char.IsControl) || ApplicationId != ApplicationId.Trim()
            || string.IsNullOrWhiteSpace(Query) || Query.Length > 200 || Query.Any(char.IsControl)
            || Query != Query.Trim() || !Enum.IsDefined(FollowUp)
            || MusicChart is not null && (MusicChart != NetEaseHotChart || LatestMusicArtist is not null
                || FollowUp == ApplicationSearchFollowUp.OpenFirst)
            || LatestMusicArtist is not null && (string.IsNullOrWhiteSpace(LatestMusicArtist)
                || LatestMusicArtist.Length > 80 || LatestMusicArtist != LatestMusicArtist.Trim()
                || LatestMusicArtist.Any(char.IsControl)
                || LatestMusicArtist is "我" or "你" or "他" or "她" or "它" or "他们" or "她们" or "它们"
                    or "这个歌手" or "这位歌手" or "那个歌手" or "歌手" or "谁"))
            throw new ArgumentException("搜索流程必须包含明确应用、有效关键词和允许的后续动作。");
    }

    public string ToCanonicalTarget(string applicationTargetBinding)
    {
        Validate();
        if (applicationTargetBinding is null || applicationTargetBinding.Length != 64
            || applicationTargetBinding.Any(ch => !Uri.IsHexDigit(ch)))
            throw new ArgumentException("搜索流程缺少应用目标绑定。", nameof(applicationTargetBinding));
        // Ordinary-search scopes retain their original exact representation. Latest
        // music lookup is a separate explicit intent and must survive every re-plan.
        if (MusicChart is not null)
            return JsonSerializer.Serialize(new
            {
                applicationId = ApplicationId,
                applicationTargetBinding = applicationTargetBinding.ToUpperInvariant(),
                query = Query,
                followUp = FollowUp.ToString(),
                musicChart = MusicChart
            });
        if (LatestMusicArtist is not null)
            return JsonSerializer.Serialize(new
            {
                applicationId = ApplicationId,
                applicationTargetBinding = applicationTargetBinding.ToUpperInvariant(),
                query = Query,
                followUp = FollowUp.ToString(),
                latestMusicArtist = LatestMusicArtist
            });
        return JsonSerializer.Serialize(new
        {
            applicationId = ApplicationId,
            applicationTargetBinding = applicationTargetBinding.ToUpperInvariant(),
            query = Query,
            followUp = FollowUp.ToString()
        });
    }
}
