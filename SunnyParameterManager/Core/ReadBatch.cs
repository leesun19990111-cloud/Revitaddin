namespace SunnyParameterManager.Core;

public sealed record ReadFailure(string Path, string Message, int Remaining, bool RestartRequired);
public sealed record ReadBatchResult(int Attempted, int Failed, int Remaining, bool Blocked);

// 조회 전용 순차 큐. 실패 파일은 재시도하지 않고, 사용자 승인 이후에만 다음 파일로 간다.
public static class ReadBatch
{
    public static async Task<ReadBatchResult> Run(IReadOnlyList<string> paths,
        Func<string, Task<Response>> read, Action<string, Response> received,
        Func<ReadFailure, bool> confirm, Func<Task> restart, Action<string> blocked)
    {
        int failed = 0;
        for (int i = 0; i < paths.Count; i++)
        {
            Response result;
            try { result = await read(paths[i]); }
            catch (Exception ex) { result = new() { RequiresAttention = true, Message = "조회 응답을 확인하지 못했습니다. " + ex.Message }; }
            if (result.Success && result.Snapshot == null)
                result = new() { RequiresAttention = true, Message = "조회 응답에 결과가 없습니다. 연결 상태를 확인하세요." };
            received(paths[i], result);
            int remaining = paths.Count - i - 1;
            if (!result.Success) failed++;
            if ((result.RequiresAttention || result.ReadRestartRequired) && !result.CanRestartForRead)
            {
                blocked("작업용 Revit에 문서가 남아 있거나 작업 완료 여부를 확인할 수 없어 계속 조회하지 않습니다.\n" + result.Message);
                return new(i + 1, failed, remaining, true);
            }
            if (!result.Success || result.RequiresAttention || result.ReadRestartRequired)
            {
                if (remaining == 0 || !confirm(new(paths[i], result.Message, remaining, result.CanRestartForRead)))
                    return new(i + 1, failed, remaining, false);
                if (result.CanRestartForRead)
                {
                    try { await restart(); }
                    catch (Exception ex)
                    {
                        blocked("새 조회 연결을 준비하지 못했습니다. 남은 파일과 기존 변경 예정은 유지됩니다.\n" + ex.Message);
                        return new(i + 1, failed, remaining, true);
                    }
                }
            }
        }
        return new(paths.Count, failed, 0, false);
    }
}
