using System.IO;

namespace SunnyParameterManager.Desktop;

internal static class WorkbookOutput
{
    // 같은 폴더에서 완성한 XLSX를 원자적으로 게시한다. 동시 내보내기도 기존 파일을 교체하지 않는다.
    internal static string Publish(string temporary, string fullPath)
    {
        var folder = Path.GetDirectoryName(fullPath)!;
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        for (int index = 1; ; index++)
        {
            var candidate = index == 1 ? fullPath : Path.Combine(folder, $"{stem}_{index}{extension}");
            try
            {
                File.Move(temporary, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException ex) when (ex.HResult is unchecked((int)0x80070050) or unchecked((int)0x800700B7))
            {
                // Windows의 파일 존재 오류만 다음 번호로 재시도한다.
            }
        }
    }
}