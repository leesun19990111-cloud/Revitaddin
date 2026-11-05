using System.Security.Cryptography;

namespace SunnyParameterManager.Core;

public static class CentralRebuildPlanner
{
    public static CentralRebuildEvidence Capture(string path)
    {
        var before = new FileInfo(path);
        if (!before.Exists || before.Length == 0) throw new IOException("중앙파일을 읽을 수 없습니다: " + path);
        var length = before.Length;
        var written = before.LastWriteTimeUtc;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        var after = new FileInfo(path);
        if (!after.Exists || length != after.Length || written != after.LastWriteTimeUtc)
            throw new IOException("중앙파일을 읽는 동안 다른 저장이 발생했습니다. 다시 조회하세요: " + path);
        return new CentralRebuildEvidence { Length = length, LastWriteUtc = written, Sha256 = hash };
    }

    public static void Validate(ModelSnapshot model)
    {
        if (model.Kind != "중앙" || model.CentralRebuild?.FormatVersion != 1 ||
            model.Query?.IncludeCentralRebuild != true ||
            !string.Equals(model.CanonicalPath, model.CentralPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(model.Path, model.CanonicalPath, StringComparison.OrdinalIgnoreCase) ||
            model.CentralRebuild.Length <= 0 || model.CentralRebuild.Sha256.Length != 64)
            throw new InvalidOperationException(model.Title + ": 파일 기반 중앙모델을 분리 재생성 전용으로 다시 조회하세요. 로컬·일반 파일과 이전 조회는 사용할 수 없습니다.");
    }

    public static void RequireUnchanged(CentralRebuildEvidence expected, CentralRebuildEvidence actual)
    {
        if (expected.FormatVersion != 1 || expected.Length != actual.Length || expected.LastWriteUtc != actual.LastWriteUtc ||
            !string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("조회 이후 중앙파일이 변경되었습니다. 다른 사용자의 저장 또는 파일 교체를 확인하고 다시 조회하세요. 이번 파일은 덮어쓰지 않았습니다.");
    }

    public static void RequireBackupContents(CentralRebuildEvidence original, CentralRebuildEvidence backup)
    {
        if (original.Length != backup.Length || !string.Equals(original.Sha256, backup.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("복구 사본의 내용이 원본과 다릅니다. 중앙파일은 덮어쓰지 않았습니다.");
    }
}
