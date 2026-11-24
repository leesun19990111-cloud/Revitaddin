namespace SunnyParameterManager.Core;

// 전송 성공 후 세션 안에서 만든 사본 폴더만 정리한다. 원본 RVT나 세션 전체는 대상이 아니다.
public static class TransferTempCleanup
{
    public static bool TryDelete(string sessionFolder, string path, out string error)
    {
        error = "";
        if (path.Length == 0) return true;
        var root = Path.GetFullPath(Path.Combine(sessionFolder, "NativeTransfer")).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var actual = Path.GetFullPath(path);
        if (!actual.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            error = "작업 폴더 범위를 벗어난 경로입니다.";
            return false;
        }
        var name = Path.GetRelativePath(root, actual);
        bool target = Guid.TryParseExact(name, "N", out _);
        bool source = name.StartsWith("기준_", StringComparison.Ordinal) &&
            Guid.TryParseExact(name["기준_".Length..], "N", out _);
        if (!target && !source)
        {
            error = "전송 작업의 사본 폴더가 아닙니다.";
            return false;
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { if (Directory.Exists(actual)) Directory.Delete(actual, true); return true; }
            catch (IOException ex) { error = ex.Message; }
            catch (UnauthorizedAccessException ex) { error = ex.Message; }
            Thread.Sleep(300);
        }
        return false;
    }
}
