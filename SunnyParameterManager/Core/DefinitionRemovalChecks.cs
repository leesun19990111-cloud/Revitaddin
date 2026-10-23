using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SunnyParameterManager.Core;

public static class DefinitionRemovalChecks
{
    public static string Fingerprint(IEnumerable<KeyValuePair<string, string>> values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new[] { p.Key, p.Value })))));
    public static ParameterDefinitionInfo Copy(ParameterDefinitionInfo value) => JsonSerializer.Deserialize<ParameterDefinitionInfo>(JsonSerializer.Serialize(value))!;
    public static bool HasEvidence(ParameterDefinitionInfo info) => info.Key.Length > 0 && info.ElementUniqueId.Length > 0 && info.ElementId.Length > 0 &&
        Guid.TryParse(info.Revision, out _) && info.ValueCount >= 0 && Regex.IsMatch(info.ValueFingerprint, @"\A[0-9A-F]{64}\z");

    public static bool SameMetadata(ParameterDefinitionInfo a, ParameterDefinitionInfo b) =>
        a.Key == b.Key && a.ElementUniqueId == b.ElementUniqueId && a.ElementId == b.ElementId && a.Revision == b.Revision &&
        a.Name == b.Name && a.Guid == b.Guid && a.Binding == b.Binding && a.DataType == b.DataType && a.Group == b.Group &&
        a.Categories.OrderBy(c => c, StringComparer.Ordinal).SequenceEqual(b.Categories.OrderBy(c => c, StringComparer.Ordinal));

    public static void CheckFresh(ParameterDefinitionInfo expected, ParameterDefinitionInfo actual, IReadOnlyDictionary<string, string> values)
    {
        if (!HasEvidence(expected) || !SameMetadata(expected, actual) || expected.ValueCount != values.Count || expected.ValueFingerprint != Fingerprint(values))
            throw new InvalidOperationException($"'{expected.Name}'의 정의·카테고리·기존 값이 조회 당시와 다릅니다. 삭제하지 않습니다. 이 파일을 다시 조회하세요. 요소 ID: {expected.ElementId}");
    }

    public static DefinitionRemovalEvidence ValidateRequest(ValueEdit edit)
    {
        var fix = edit.DefinitionFix;
        var proof = fix?.Removal;
        if (fix?.Action != SpecRulePlanner.ActionRemove || proof?.Version != 1 || !HasEvidence(proof.Source) ||
            edit.AddParameter != null || edit.KacCleanup || edit.MaterialSource != null || edit.Mode != "입력" ||
            edit.Original.Scope != SpecRulePlanner.DefinitionScope || edit.Original.ParameterKey != proof.Source.Key ||
            edit.Original.OwnerId != "def:" + SpecRulePlanner.ActionRemove + ":" + proof.Source.Key ||
            edit.Original.Name != proof.Source.Name || fix.SourceName != proof.Source.Name || fix.ElementUniqueId != proof.Source.ElementUniqueId ||
            fix.ElementId != proof.Source.ElementId || fix.Binding != proof.Source.Binding || fix.ValueCount != proof.Source.ValueCount ||
            !proof.AllowedCategories.SequenceEqual(new[] { "OST_ProjectInformation" }) ||
            !proof.Source.Categories.OrderBy(c => c, StringComparer.Ordinal).SequenceEqual(proof.AllowedCategories))
            throw new InvalidOperationException("삭제 요청의 정확한 이름·정의·프로젝트 정보 단독 연결 근거가 없거나 다릅니다. 새 룰셋으로 다시 조회하세요.");
        if (proof.Keeper is { } keeper)
        {
            if (!HasEvidence(keeper) || !keeper.Categories.Contains("OST_ProjectInformation", StringComparer.Ordinal) || fix.TargetKey != keeper.Key || fix.Name != keeper.Name || keeper.Name == proof.Source.Name || keeper.Key == proof.Source.Key)
                throw new InvalidOperationException("남길 매개변수의 승인 근거가 다릅니다. 삭제하지 않습니다.");
        }
        else if (fix.TargetKey.Length > 0 || fix.Name != proof.Source.Name)
            throw new InvalidOperationException("남길 매개변수의 승인 정보가 일치하지 않습니다.");
        return proof;
    }
}
