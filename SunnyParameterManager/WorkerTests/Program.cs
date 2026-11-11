using SunnyParameterManager.Core;
using SunnyParameterManager.Bridge;
using Autodesk.Revit.UI;
using System.Reflection;

internal static class Program
{
    static int count;
    static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); count++; Console.WriteLine("PASS " + name); }
    static void Set(WorkerApplication worker, string name, object value) => typeof(WorkerApplication).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(worker, value);
    public static int Main()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sunny-worker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var worker = new WorkerApplication();
            var ui = new UIApplication();
            Set(worker, "_folder", folder);
            Response Run(string operation)
            {
                var request = new Request { Operation = operation };
                Set(worker, "_pending", request);
                worker.Execute(ui);
                return Protocol.Read<Response>(Path.Combine(folder, request.Id + ".result.json"));
            }
            void Reset()
            {
                worker = new(); Set(worker, "_folder", folder); ui = new();
                ModelEngine.Result = new() { Success = true, Snapshot = new() };
                ModelEngine.Error = null; ModelEngine.LeaveDocument = false; ModelEngine.Calls = 0;
                CentralRebuildEngine.Result = new() { Success = true }; CentralRebuildEngine.Calls = 0;
                NativeProjectTransferSession.Created = NativeProjectTransferSession.Started = 0;
            }
            Reset(); ModelEngine.Result = new() { ReadRestartRequired = true, Message = "OOM" };
            var result = Run("조회");
            Check(result.RequiresAttention && result.CanRestartForRead && !result.Success, "조회 메모리 실패·빈 문서 확인 후에만 재시작 승인");
            result = Run("적용");
            Check(!result.Success && result.RequiresAttention && !result.CanRestartForRead && ModelEngine.Calls == 1, "조회 복구 잠금 중에도 적용 요청 거부");
            Reset(); ModelEngine.Result = new() { ReadRestartRequired = true };
            Run("조회"); result = Run("종료");
            Check(result.Success && ui.Posted == 1 && ModelEngine.Calls == 1, "조회 복구 때 빈 프로세스에 정상 종료 명령만 게시");
            Reset(); ModelEngine.Result = new() { ReadRestartRequired = true };
            Run("조회"); ui.Application.Documents.Add(new()); result = Run("종료");
            Check(!result.Success && ui.Posted == 0, "승인 이후에도 문서가 생기면 종료 금지");
            Reset(); ModelEngine.Result = new() { ReadRestartRequired = true }; ModelEngine.LeaveDocument = true;
            result = Run("조회");
            Check(result.RequiresAttention && !result.CanRestartForRead, "조회 OOM 뒤 문서가 남으면 복구 잠금 유지");
            Reset(); ModelEngine.Error = new OutOfMemoryException("결과 전달 실패");
            result = Run("조회");
            Check(result.CanRestartForRead && result.RequiresAttention, "조회 결과 전달 예외·빈 문서는 새 연결 허용");
            Reset(); ModelEngine.Error = new OutOfMemoryException(); ModelEngine.LeaveDocument = true;
            result = Run("조회");
            Check(!result.CanRestartForRead && result.RequiresAttention, "조회 예외에도 문서 확인을 생략하지 않음");
            Reset(); ModelEngine.Error = new OutOfMemoryException();
            result = Run("적용");
            Check(result.RequiresAttention && !result.CanRestartForRead, "적용 OOM은 조회 재시작 경로로 해제 불가");
            result = Run("조회");
            Check(result.RequiresAttention && !result.CanRestartForRead && ModelEngine.Calls == 1, "기존 쓰기 복구 잠금을 조회 요청으로 우회 불가");
            result = Run("종료");
            Check(!result.Success && ui.Posted == 0, "쓰기 복구 문서 정상 종료도 자동 요청 금지");
            Reset(); ui.Application.Documents.Add(new()); result = Run("조회");
            Check(!result.CanRestartForRead && ModelEngine.Calls == 0, "사용자가 연 문서 있는 작업용 Revit에 조회하지 않음");
            Reset(); ModelEngine.Result = new() { Message = "일반 실패" }; result = Run("조회");
            Check(!result.RequiresAttention && !result.CanRestartForRead, "일반 조회 실패는 빈 세션을 불필요하게 잠그지 않음");
            Reset(); ModelEngine.Result = new() { CanContinueAfterPreflightFailure = true }; result = Run("적용");
            Check(result.CanContinueAfterPreflightFailure && !result.RequiresAttention, "사전 실패 뒤 실제 문서 없음 확인 시 계속 후보");
            Reset(); ModelEngine.Result = new() { CanContinueAfterPreflightFailure = true }; ModelEngine.LeaveDocument = true; result = Run("적용");
            Check(!result.CanContinueAfterPreflightFailure && result.RequiresAttention, "사전 실패라도 문서 잔존 시 전체 중단");
            result = Run("적용"); Check(result.RequiresAttention && ModelEngine.Calls == 1, "잔존 문서 후 후속 적용 차단");
            Reset(); ModelEngine.Result = new() { CanContinueAfterPreflightFailure = true }; result = Run("조회");
            Check(!result.CanContinueAfterPreflightFailure, "조회 응답은 적용 계속 승인 불가");
            Reset(); ModelEngine.Result = new() { CanContinueAfterPreflightFailure = true, CentralSynchronized = true }; result = Run("적용");
            Check(!result.CanContinueAfterPreflightFailure, "중앙 반영된 응답은 사전 실패로 오인 금지");
            Reset(); result = Run("중앙 재생성");
            Check(result.Success && CentralRebuildEngine.Calls == 1 && ModelEngine.Calls == 0, "중앙 재생성 요청은 전용 엔진으로만 전달");
            Reset(); CentralRebuildEngine.Result = new() { RequiresAttention = true, Stage = "중앙 교체 결과 확인 필요" }; result = Run("중앙 재생성");
            Check(result.RequiresAttention && !result.Success, "중앙 교체 결과 불명은 작업 세션 잠금");
            result = Run("조회");
            Check(result.RequiresAttention && ModelEngine.Calls == 0, "중앙 교체 결과 불명 뒤 후속 조회 차단");
            Reset();
            Check(Run("프로젝트 매개변수 전송").Success && Run("프로젝트 표준 전송 완료").Success &&
                Run("프로젝트 매개변수 전송").Success && Run("프로젝트 표준 전송 완료").Success &&
                NativeProjectTransferSession.Created == 1 && NativeProjectTransferSession.Started == 2,
                "기준 문서를 유지한 채 대상 두 건을 순차 처리");
            Check(Run("프로젝트 표준 전송 묶음 종료").Success && ui.Application.Documents.Count == 0 &&
                Run("조회").Success, "묶음 종료 후 기준 문서 정리·일반 조회 재개");
            Reset();
            Run("프로젝트 매개변수 전송"); Run("프로젝트 표준 전송 완료");
            ui.Application.Documents.Add(new() { PathName = "사용자 문서" });
            result = Run("프로젝트 매개변수 전송");
            Check(!result.Success && result.RequiresAttention && NativeProjectTransferSession.Started == 1,
                "기준 문서 외 다른 문서가 있으면 다음 전송 거부");
            Console.WriteLine($"PASS {count} worker 안전 경계 검사"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(folder, true); }
    }
}
