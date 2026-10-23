using Autodesk.Revit.UI;
using SunnyParameterManager.Core;
using System.Diagnostics;

namespace SunnyParameterManager.Bridge;

// 외부 프로그램이 시작한 전용 프로세스에서만 활성화한다. 일반 Revit 세션에는 이벤트도 등록하지 않는다.
public sealed class WorkerApplication : IExternalApplication, IExternalEventHandler
{
    private string _folder = "";
    private string _token = "";
    private ExternalEvent? _event;
    private Timer? _timer;
    private int _busy;
    private Request? _pending;
    private bool _attention;
    private bool _readRestartAllowed;

    public Result OnStartup(UIControlledApplication app)
    {
        try
        {
            var id = Environment.GetEnvironmentVariable("SUNNY_PARAMETER_SESSION");
            var token = Environment.GetEnvironmentVariable("SUNNY_PARAMETER_TOKEN");
            if (!Guid.TryParseExact(id, "N", out _) || token?.Length != 64) return Result.Succeeded;
            _folder = Protocol.SessionFolder(id!);
            _token = token;
            if (!Directory.Exists(_folder)) return Result.Succeeded;
            _event = ExternalEvent.Create(this);
            _timer = new Timer(_ => Poll(), null, 1000, 500);
            Protocol.Write(Path.Combine(_folder, "ready.json"), new WorkerReady { ProcessId = Environment.ProcessId, RevitVersion = app.ControlledApplication.VersionNumber });
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            SafeWrite("startup-error.json", new Response { Message = ex.Message, RequiresAttention = true });
            return Result.Succeeded;
        }
    }

    private void Poll()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            var path = Path.Combine(_folder, "request.json");
            if (!File.Exists(path)) { Interlocked.Exchange(ref _busy, 0); return; }
            var req = Protocol.Read<Request>(path);
            if (req.Token != _token || req.ProtocolVersion != Protocol.Version || !Guid.TryParseExact(req.Id, "N", out _))
                throw new InvalidDataException("작업 인증 또는 프로그램 버전이 일치하지 않습니다.");
            // 요청 파일을 먼저 소비한다. 종료/응답 손실 이후 같은 작업을 자동 재실행하지 않는다.
            File.Move(path, Path.Combine(_folder, req.Id + ".claimed.json"), false);
            _pending = req;
            var result = _event!.Raise();
            if (result != ExternalEventRequest.Accepted)
                throw new InvalidOperationException("Revit이 요청을 받지 못했습니다: " + result);
        }
        catch (Exception ex)
        {
            var req = _pending;
            SafeWrite(req == null ? "worker-error.json" : req.Id + ".result.json", new Response { Id = req?.Id ?? "", Message = ex.Message, Stage = "요청 실패", RequiresAttention = true });
            _pending = null;
            _attention = true;
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Execute(UIApplication ui)
    {
        var req = _pending;
        try
        {
            if (req == null) return;
            if (_attention && !(req.Operation == "종료" && _readRestartAllowed)) throw new InvalidOperationException("이 작업 세션은 확인이 필요합니다. 결과와 작업용 Revit을 확인한 뒤 종료하세요. 자동 재실행하지 않습니다.");
            if (ui.Application.VersionNumber != "2026") throw new InvalidOperationException("첫 버전은 Revit 2026만 지원합니다.");
            if (ui.Application.Documents.Cast<Autodesk.Revit.DB.Document>().Any())
                throw new InvalidOperationException("작업용 Revit에 이미 문서가 열려 있습니다. 사용자 문서나 복구 문서를 임의로 닫거나 수정하지 않습니다.");
            if (req.Operation == "종료")
            {
                var command = RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit);
                if (!ui.CanPostCommand(command)) throw new InvalidOperationException("현재 Revit을 종료할 수 없습니다.");
                SafeWrite(req.Id + ".result.json", new Response { Id = req.Id, Success = true, Stage = "종료 요청", Message = "빈 작업용 Revit에 종료를 요청했습니다." });
                ui.PostCommand(command);
                return;
            }
            var engine = new ModelEngine(_folder, r => SafeWrite(req.Id + ".progress.json", r));
            var result = engine.Run(ui.Application, req);
            if (req.Operation == "조회" && result.ReadRestartRequired)
            {
                result.RequiresAttention = true;
                result.CanRestartForRead = !HasDocuments(ui);
                _readRestartAllowed = result.CanRestartForRead;
                result.Message += "\n조회 중 메모리가 부족했습니다. 불완전한 조회 결과는 사용하지 않습니다.";
            }
            if (result.CanContinueAfterPreflightFailure)
            {
                bool empty = !HasDocuments(ui);
                result.CanContinueAfterPreflightFailure = req.Operation == "적용" && !result.Success && !result.RequiresAttention &&
                    !result.CentralSynchronized && result.Applied == 0 && empty;
                if (!empty) result.RequiresAttention = true;
            }
            _attention |= result.RequiresAttention;
            Protocol.Write(Path.Combine(_folder, req.Id + ".result.json"), result);
        }
        catch (Exception ex)
        {
            // 조회 결과 전달 실패도 저장 실패와 구별한다. 기존 잠금이나 남은 문서는 절대 우회하지 않는다.
            bool restart = (!_attention || _readRestartAllowed) && req?.Operation == "조회" && !HasDocuments(ui);
            _readRestartAllowed = restart;
            _attention = true;
            SafeWrite((req?.Id ?? "worker-error") + ".result.json", new Response { Id = req?.Id ?? "", Stage = restart ? "조회 실패 · 연결 재시작 가능" : "확인 필요", Message = ex.Message, RequiresAttention = true, CanRestartForRead = restart });
        }
        finally { _pending = null; Interlocked.Exchange(ref _busy, 0); }
    }

    private void SafeWrite(string file, Response response) { try { if (_folder.Length > 0) Protocol.Write(Path.Combine(_folder, file), response); } catch { /* 기록 실패가 Revit으로 전파되지 않게 한다. */ } }
    private static bool HasDocuments(UIApplication ui)
    {
        try { return ui.Application.Documents.Cast<Autodesk.Revit.DB.Document>().Any(); }
        catch { return true; } // 확인 불가는 빈 작업 프로세스로 간주하지 않는다.
    }
    public string GetName() => "Sunny 매개변수 관리자 전용 작업";
    public Result OnShutdown(UIControlledApplication app)
    {
        try { _timer?.Dispose(); _event?.Dispose(); } catch { /* 종료 중 예외를 외부로 보내지 않는다. */ }
        return Result.Succeeded;
    }
}
