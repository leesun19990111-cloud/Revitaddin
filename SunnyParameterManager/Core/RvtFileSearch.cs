using System.Text.RegularExpressions;

namespace SunnyParameterManager.Core;

public sealed class RvtSearchResult
{
    public List<string> Files { get; } = [];
    public List<string> Warnings { get; } = [];
    public int SkippedBackups { get; internal set; }
}

// 파일 내용/Revit에 접근하지 않는 후보 검색. 재분석 지점은 따라가지 않는다.
public static class RvtFileSearch
{
    public static RvtSearchResult Find(IEnumerable<string> roots, string contains, bool recursive, bool excludeBackups,
        CancellationToken cancellation = default)
    {
        contains = contains.Trim();
        if (contains.Length == 0) throw new InvalidOperationException("파일명에 포함할 문자열을 입력하세요. 예: -SP 또는 MWX");
        var result = new RvtSearchResult();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase));
        if (pending.Count == 0) throw new InvalidOperationException("검색할 폴더를 추가하세요.");
        int entries = 0;
        void Warn(string text) { if (result.Warnings.Count < 200) result.Warnings.Add(text); }
        while (pending.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            if (!visited.Add(Path.TrimEndingDirectorySeparator(folder))) continue;
            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) { Warn("연결 폴더 제외: " + folder); continue; }
                if (excludeBackups && Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)).EndsWith("_backup", StringComparison.OrdinalIgnoreCase))
                { result.SkippedBackups++; continue; }
                foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (++entries > 200000) throw new InvalidOperationException("검색 범위가 너무 큽니다(20만 항목 초과). 폴더 범위를 좁혀 다시 검색하세요.");
                    try
                    {
                        var attrs = File.GetAttributes(path);
                        if ((attrs & FileAttributes.ReparsePoint) != 0) { Warn("연결 경로 제외: " + path); continue; }
                        if ((attrs & FileAttributes.Directory) != 0) { if (recursive) pending.Push(path); continue; }
                        if (!Path.GetExtension(path).Equals(".rvt", StringComparison.OrdinalIgnoreCase)) continue;
                        if (excludeBackups && Regex.IsMatch(Path.GetFileName(path), @"\.\d{4}\.rvt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        { result.SkippedBackups++; continue; }
                        if (Path.GetFileName(path).Contains(contains, StringComparison.OrdinalIgnoreCase)) found.Add(Path.GetFullPath(path));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn(path + " · " + ex.Message); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn(folder + " · " + ex.Message); }
        }
        result.Files.AddRange(found.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p, StringComparer.OrdinalIgnoreCase));
        return result;
    }
}
