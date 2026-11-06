using System.Security.Cryptography;

namespace SunnyParameterManager.Core;

public static class CentralRebuildPlanner
{
    public static CentralRebuildEvidence Capture(string path, string sourceKind)
    {
        if (sourceKind is not ("중앙" or "작업공유 사본"))
            throw new InvalidOperationException("선택한 RVT의 종류를 확인할 수 없습니다.");
        var before = new FileInfo(path);
        if (!before.Exists || before.Length == 0) throw new IOException("선택한 RVT를 읽을 수 없습니다: " + path);
        var length = before.Length;
        var written = before.LastWriteTimeUtc;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        var after = new FileInfo(path);
        if (!after.Exists || length != after.Length || written != after.LastWriteTimeUtc)
            throw new IOException("선택한 RVT를 읽는 동안 다른 저장이 발생했습니다. 다시 조회하세요: " + path);
        return new CentralRebuildEvidence { SourceKind = sourceKind, Length = length, LastWriteUtc = written, Sha256 = hash };
    }

    public static string Identity(Guid creationId, string canonicalPath) =>
        "중앙재생성-v2/" + creationId.ToString("D") + "/" + canonicalPath.ToUpperInvariant();

    public static void Validate(ModelSnapshot model)
    {
        if (model.CentralRebuild?.FormatVersion != 2 || model.CentralRebuild.SourceKind != model.Kind ||
            model.Kind is not ("중앙" or "작업공유 사본") ||
            model.Query?.IncludeCentralRebuild != true ||
            string.IsNullOrWhiteSpace(model.Path) || string.IsNullOrWhiteSpace(model.CanonicalPath) ||
            !Path.IsPathFullyQualified(model.CanonicalPath) ||
            !model.Identity.StartsWith("중앙재생성-v2/", StringComparison.Ordinal) ||
            !model.Identity.EndsWith("/" + model.CanonicalPath.ToUpperInvariant(), StringComparison.Ordinal) ||
            model.CentralRebuild.Length <= 0 || model.CentralRebuild.Sha256.Length != 64)
            throw new InvalidOperationException(model.Title + ": 선택한 RVT를 중앙 재생성 룰셋으로 다시 조회하세요. 이전 조회는 사용할 수 없습니다.");
    }

    public static void RequireUnchanged(CentralRebuildEvidence expected, CentralRebuildEvidence actual)
    {
        if (expected.FormatVersion != 2 || actual.FormatVersion != 2 || expected.SourceKind != actual.SourceKind ||
            expected.Length != actual.Length || expected.LastWriteUtc != actual.LastWriteUtc ||
            !string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("조회 이후 선택한 RVT가 변경되었습니다. 다른 사용자의 저장 또는 파일 교체를 확인하고 다시 조회하세요. 이번 파일은 덮어쓰지 않았습니다.");
    }

    public static void RequireBackupContents(CentralRebuildEvidence original, CentralRebuildEvidence backup)
    {
        if (original.Length != backup.Length || !string.Equals(original.Sha256, backup.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("복구 사본의 내용이 원본과 다릅니다. 중앙파일은 덮어쓰지 않았습니다.");
    }
}
