using System;
using System.IO;
using SunnyParameterManager.Core;

internal static class TransferTempCleanupTests
{
    public static void Run(Action<bool, string> check)
    {
        var session = Path.Combine(Path.GetTempPath(), "sunny-transfer-cleanup-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(session, "NativeTransfer");
        var target = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "기준_" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(session, "원본.rvt");
        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(target, "대상.rvt"), "임시 대상");
            File.WriteAllText(Path.Combine(source, "기준.rvt"), "임시 기준");
            File.WriteAllText(outside, "원본");
            check(!TransferTempCleanup.TryDelete(session, outside, out _) && File.Exists(outside),
                "원본 RVT 경로는 정리 대상에서 제외");
            check(!TransferTempCleanup.TryDelete(session, root, out _) &&
                !TransferTempCleanup.TryDelete(session, Path.Combine(target, "하위"), out _) &&
                Directory.Exists(target) && Directory.Exists(source),
                "전송 루트·임의 하위 경로는 삭제 거부");
            check(TransferTempCleanup.TryDelete(session, target, out _) &&
                !Directory.Exists(target) && Directory.Exists(source) && File.Exists(outside),
                "완료된 대상 사본만 삭제하고 기준·원본 보존");
            check(TransferTempCleanup.TryDelete(session, source, out _) &&
                !Directory.Exists(source) && File.Exists(outside),
                "기준 문서가 닫힌 뒤 기준 사본만 정리");
        }
        finally { if (Directory.Exists(session)) Directory.Delete(session, true); }
    }
}
