using SunnyParameterManager.Core;
using System.IO;

internal static class ReadBatchTests
{
    public static void Run(Action<bool, string> check)
    {
        static Response Good() => new() { Success = true, Snapshot = new() };
        List<string> calls = [], received = [], blocked = [];
        List<ReadFailure> prompts = [];
        int restarts = 0;
        bool answer = true, failRestart = false;
        var responses = new Queue<Response>();
        async Task<ReadBatchResult> Batch(params Response[] values)
        {
            calls.Clear(); received.Clear(); blocked.Clear(); prompts.Clear(); restarts = 0;
            responses = new(values);
            return await ReadBatch.Run(["A", "B", "C"], path => { calls.Add(path); return Task.FromResult(responses.Dequeue()); },
                (path, _) => received.Add(path), failure => { prompts.Add(failure); return answer; },
                () => { restarts++; if (failRestart) throw new IOException("종료 실패"); return Task.CompletedTask; }, blocked.Add);
        }
        var result = Batch(Good(), new() { Message = "실패 원인" }, Good()).GetAwaiter().GetResult();
        check(calls.SequenceEqual(new[] { "A", "B", "C" }) && result.Failed == 1, "조회 실패 승인 후 다음 파일 계속");
        check(prompts.Count == 1 && prompts[0] == new ReadFailure("B", "실패 원인", 1, false), "실패 팝업 파일·사유·남은 수 정확");
        check(received.SequenceEqual(calls) && restarts == 0 && blocked.Count == 0, "완료 결과 순서 유지·일반 실패 재연결 없음");
        answer = false;
        result = Batch(Good(), new(), Good()).GetAwaiter().GetResult();
        check(calls.SequenceEqual(new[] { "A", "B" }) && result.Remaining == 1 && !result.Blocked, "아니요는 남은 파일 미조회 상태로 중단");
        answer = true;
        result = Batch(new(), new(), Good()).GetAwaiter().GetResult();
        check(prompts.Count == 2 && result.Failed == 2 && restarts == 0, "연속 실패마다 별도 승인·실패 파일 자동 재시도 없음");
        var restart = new Response { RequiresAttention = true, ReadRestartRequired = true, CanRestartForRead = true };
        result = Batch(restart, Good(), Good()).GetAwaiter().GetResult();
        check(restarts == 1 && prompts.Single().RestartRequired && result.Attempted == 3, "안전 확인된 조회 메모리 실패만 승인 후 재시작");
        answer = false;
        result = Batch(restart, Good(), Good()).GetAwaiter().GetResult();
        check(restarts == 0 && calls.Count == 1, "메모리 실패에서 아니요면 작업용 Revit 종료도 하지 않음");
        answer = true; failRestart = true;
        result = Batch(restart, Good(), Good()).GetAwaiter().GetResult();
        check(result.Blocked && result.Remaining == 2 && calls.Count == 1 && blocked.Single().Contains("종료 실패"), "재시작 실패 시 다음 파일 조회 금지");
        failRestart = false;
        result = Batch(new() { RequiresAttention = true }, Good(), Good()).GetAwaiter().GetResult();
        check(result.Blocked && prompts.Count == 0 && restarts == 0 && calls.Count == 1, "미확인 문서·저장 상태는 승인으로 우회 불가");
        result = Batch(Good(), Good(), new()).GetAwaiter().GetResult();
        check(prompts.Count == 0 && result.Remaining == 0 && result.Failed == 1, "마지막 파일 실패에 남은 파일 없는 계속 팝업 없음");
        result = Batch(new() { Success = true }, Good(), Good()).GetAwaiter().GetResult();
        check(result.Blocked && calls.Count == 1 && prompts.Count == 0, "결과 누락 응답을 조회 성공으로 사용하지 않음");
        result = ReadBatch.Run(["A", "B"], _ => throw new IOException("응답 손실"), (_, _) => { }, _ => throw new Exception("승인 금지"), () => throw new Exception("재시작 금지"), blocked.Add).GetAwaiter().GetResult();
        check(result.Blocked && result.Remaining == 1, "연결 상태 미확인 예외는 안전 중단");
        string dir = Path.Combine(Path.GetTempPath(), "sunny-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "test.json");
            Protocol.Write(path, new Response { Message = "한글 왕복", CanRestartForRead = true });
            var read = Protocol.Read<Response>(path);
            check(read.Message == "한글 왕복" && read.CanRestartForRead, "스트림 프로토콜 한글·복구 필드 왕복");
            bool failed = false;
            try { Protocol.Write(path, new Broken()); } catch { failed = true; }
            check(failed && Protocol.Read<Response>(path).Message == "한글 왕복", "직렬화 실패 시 이전 완성 응답 보존");
            check(Directory.GetFiles(dir).Length == 1, "직렬화 실패 임시 파일 정리");
        }
        finally { Directory.Delete(dir, true); }
    }
    private sealed class Broken { public string Value => throw new InvalidOperationException("직렬화 실패 대역"); }
}
