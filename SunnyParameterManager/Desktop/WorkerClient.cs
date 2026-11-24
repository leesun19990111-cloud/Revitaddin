using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SunnyParameterManager.Core;

namespace SunnyParameterManager.Desktop;

internal sealed record WorkerRecovery(string Folder, Request? Request, Response? Result, string Detail, int? ProcessId, bool CanContinue);

public sealed class WorkerClient
{
    private Process? _process;
    private string _folder = "";
    private string _token = "";
    private bool _sending;
    private Request? _attentionRequest;
    private string _attentionDetail = "";
    public bool Connected { get; private set; }
    public bool NeedsAttention { get; private set; }
    public bool CanRestartForRead { get; private set; }
    public string SessionFolder => _folder;
    internal Request? InterruptedRequest => _attentionRequest;
    internal bool HasRunningWorker => _process is { HasExited: false };

    internal void ShowWorkerWindow()
    {
        if (_process is not { HasExited: false } process)
            throw new InvalidOperationException("작업용 Revit이 이미 종료되었습니다.");
        NativeTransferDialog.RestoreWorkerWindow(process.Id);
    }
    public Action<string>? Progress { get; set; }

    public async Task Connect(string revitExe)
    {
        if (NeedsAttention) throw new InvalidOperationException("상단 ‘작업 결과 확인’에서 이전 작업을 확인하세요.");
        if (Connected && _process is { HasExited: false }) return;
        Connected = false;
        if (_process is { HasExited: false }) throw new InvalidOperationException("이전에 시작한 작업용 Revit이 남아 있습니다. 그 창의 시작/오류 상태를 확인하고 종료한 뒤 다시 연결하세요.");
        if (!File.Exists(revitExe) || !string.Equals(Path.GetFileName(revitExe), "Revit.exe", StringComparison.OrdinalIgnoreCase) || FileVersionInfo.GetVersionInfo(revitExe).FileMajorPart != 26)
            throw new InvalidOperationException("Revit 2026의 Revit.exe를 지정하세요.");
        var manifest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", "2026", "SunnyParameterManager.addin");
        if (!File.Exists(manifest)) throw new InvalidOperationException("연결 모듈이 설치되지 않았습니다. 배포 폴더의 SunnyTools_Install.exe로 매개변수 관리자를 설치하세요.");
        _folder = Protocol.SessionFolder(Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_folder);
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo(revitExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(revitExe)!, WindowStyle = ProcessWindowStyle.Minimized };
        start.Environment["SUNNY_PARAMETER_SESSION"] = Path.GetFileName(_folder);
        start.Environment["SUNNY_PARAMETER_TOKEN"] = _token;
        _process = Process.Start(start) ?? throw new InvalidOperationException("작업용 Revit을 시작하지 못했습니다.");
        NeedsAttention = false;
        CanRestartForRead = false;
        Progress?.Invoke("작업용 Revit 시작 중… 로그인·애드인 확인 창이 나타나면 직접 확인해 주세요. 기존 Revit은 건드리지 않습니다.");
        try
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromMinutes(3))
            {
                if (_process.HasExited) throw new InvalidOperationException("작업용 Revit이 연결 전에 종료되었습니다. 설치와 Revit 저널을 확인하세요.");
                var error = Path.Combine(_folder, "startup-error.json");
                if (File.Exists(error)) throw new InvalidOperationException(Protocol.Read<Response>(error).Message);
                var ready = Path.Combine(_folder, "ready.json");
                if (File.Exists(ready))
                {
                    var state = Protocol.Read<WorkerReady>(ready);
                    if (state.ProcessId != _process.Id || state.ProtocolVersion != Protocol.Version || state.Release != Protocol.Release || state.RevitVersion != "2026")
                        throw new InvalidOperationException("작업용 Revit 연결 모듈의 버전 또는 프로세스가 일치하지 않습니다. 최신 설치기를 실행하세요.");
                    Connected = true; Progress?.Invoke("전용 Revit 연결 완료 · 대상 파일을 미리 열 필요가 없습니다."); return;
                }
                await Task.Delay(500);
            }
            throw new TimeoutException("연결 대기 시간이 지났습니다. 작업용 Revit의 시작/로그인/애드인 안내를 확인하세요.");
        }
        catch (Exception ex)
        {
            NeedsAttention = true;
            _attentionDetail = ex.Message;
            throw;
        }
    }

    public async Task<Response> Send(Request request)
    {
        if (request.Operation != "프로젝트 매개변수 전송") return await SendCore(request);
        var started = await SendCore(request);
        if (!started.Success) return started;
        try
        {
            await NativeTransferDialog.Run(_process!.Id, started.Message, Progress);
            return await SendCore(new Request { Operation = "프로젝트 표준 전송 완료", Identity = request.Identity,
                Path = request.Path, CanonicalPath = request.CanonicalPath, TransferSource = request.TransferSource });
        }
        catch (Exception ex)
        {
            NeedsAttention = true;
            _attentionRequest ??= request;
            _attentionDetail = ex.Message;
            throw;
        }
    }

    private async Task<Response> SendCore(Request request)
    {
        bool recoveryExit = request.Operation == "복구 확인 후 종료";
        if (!_sending && Connected && !NeedsAttention && request.Operation == "조회" && _process is { HasExited: true })
        {
            NeedsAttention = true; CanRestartForRead = true;
            _attentionRequest = request;
            _attentionDetail = "다음 파일 조회 전에 작업용 Revit이 종료되었습니다.";
            return new Response { Id = request.Id, RequiresAttention = true, CanRestartForRead = true, Message = "다음 파일 조회 전에 작업용 Revit이 종료되었습니다. 새 연결이 필요합니다." };
        }
        if (!Connected || _process == null || _process.HasExited || (NeedsAttention && !recoveryExit && !(request.Operation == "종료" && CanRestartForRead))) throw new InvalidOperationException("작업용 Revit을 먼저 연결하세요. 이전 작업 확인이 필요하면 해당 Revit을 확인한 뒤 다시 시작하세요.");
        if (_sending) throw new InvalidOperationException("진행 중인 작업이 있습니다.");
        _sending = true;
        try
        {
            request.Token = _token;
            Protocol.Write(Path.Combine(_folder, "request.json"), request, overwrite: false);
            var resultPath = Path.Combine(_folder, request.Id + ".result.json");
            var watch = Stopwatch.StartNew();
            var limit = recoveryExit ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(45);
            while (watch.Elapsed < limit)
            {
                if (File.Exists(resultPath))
                {
                    Response result;
                    try { result = await Task.Run(() => Protocol.Read<Response>(resultPath)); }
                    catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
                    {
                        await Task.Delay(250);
                        continue;
                    }
                    if (result.Id != request.Id) throw new InvalidDataException("작업 응답 식별자가 다릅니다.");
                    if (!recoveryExit)
                    {
                        NeedsAttention |= result.RequiresAttention;
                        if (result.RequiresAttention)
                        {
                            _attentionRequest ??= request;
                            _attentionDetail = result.Message;
                        }
                        if (request.Operation != "종료") CanRestartForRead = request.Operation == "조회" && result.RequiresAttention && result.CanRestartForRead;
                        else if (!result.Success) CanRestartForRead = false;
                    }
                    result.Snapshot?.ConnectCatalogs();
                    if (request.Operation == "종료") Connected = false;
                    return result;
                }
                if (_process.HasExited) throw new InvalidOperationException("작업용 Revit이 종료되었습니다. 특히 저장/동기화 중이었다면 반영 여부를 확인해야 합니다. 자동 재실행하지 않습니다.");
                var error = Path.Combine(_folder, "worker-error.json");
                if (!recoveryExit && File.Exists(error)) throw new InvalidOperationException(Protocol.Read<Response>(error).Message);
                var progressPath = Path.Combine(_folder, request.Id + ".progress.json");
                if (File.Exists(progressPath) && TryReadProgress(progressPath, request.Id) is { } progress)
                    Progress?.Invoke(progress.Stage + $" · {watch.Elapsed:mm\\:ss}");
                await Task.Delay(750);
            }
            throw new TimeoutException(recoveryExit
                ? "아직 작업용 Revit을 종료할 수 없습니다. 해당 창의 안내를 확인하고 다시 ‘작업 결과 확인’을 눌러주세요."
                : "45분 대기 시간이 지났습니다. 작업용 Revit의 상태를 확인한 뒤 ‘작업 결과 확인’을 눌러주세요.");
        }
        catch (Exception ex)
        {
            // 조회 중 프로세스가 이미 종료된 경우만 새 연결을 허용한다. 살아 있는 프로세스의 응답 손실은 임의로 취소하지 않는다.
            if (recoveryExit) throw;
            bool restart = request.Operation == "조회" && !NeedsAttention && _process.HasExited;
            NeedsAttention = true; CanRestartForRead = restart;
            _attentionRequest ??= request;
            _attentionDetail = ex.Message;
            if (restart) return new Response { Id = request.Id, RequiresAttention = true, CanRestartForRead = true, Message = "조회 중 작업용 Revit이 종료되었습니다. " + ex.Message };
            throw;
        }
        finally { _sending = false; }
    }

    private static Response? TryReadProgress(string path, string id)
    {
        // 진행률은 참고 화면이다. 공유 잠금이나 교체 순간을 만나도 최종 결과는 계속 기다린다.
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var state = JsonSerializer.Deserialize<Response>(stream, Protocol.Json);
            return state?.Id == id ? state : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal async Task<WorkerRecovery> InspectRecovery()
    {
        if (!NeedsAttention || _sending) throw new InvalidOperationException("확인할 이전 작업이 없습니다.");
        var detail = _attentionDetail;
        Response? result = null;
        if (_attentionRequest is { } request)
        {
            var path = Path.Combine(_folder, request.Id + ".result.json");
            if (File.Exists(path))
            {
                try
                {
                    var response = await Task.Run(() => Protocol.Read<Response>(path));
                    if (response.Id == request.Id) result = response;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    detail += "\n최종 결과 파일을 아직 읽을 수 없습니다.";
                }
            }
            if (result == null && TryReadProgress(Path.Combine(_folder, request.Id + ".progress.json"), request.Id) is { } progress)
            {
                detail += "\n마지막 진행 기록 · " + progress.Stage;
                if (progress.RecoveryPath.Length > 0) detail += "\n복구 경로\n" + progress.RecoveryPath;
            }
        }
        // 프로세스가 살아 있으면 기존 요청의 최종 결과를 확인한 뒤에만 종료 요청을 보낸다.
        bool exited = _process is null or { HasExited: true };
        return new(_folder, _attentionRequest, result, detail, exited ? null : _process!.Id,
            exited || (Connected && result != null && !File.Exists(Path.Combine(_folder, "request.json"))));
    }

    internal async Task CompleteRecovery(WorkerRecovery review)
    {
        if (!NeedsAttention || _sending || review.Folder != _folder || review.Request != _attentionRequest)
            throw new InvalidOperationException("작업 상태가 달라졌습니다. 이전 결과를 다시 확인하세요.");
        if (_process is { HasExited: false } process)
        {
            if (!review.CanContinue || !Connected || review.Result == null)
                throw new InvalidOperationException("이전 작업이 진행 중이거나 아직 최종 결과를 확인할 수 없습니다. 작업용 Revit의 상태를 확인하세요.");
            try
            {
                var result = await SendCore(new Request { Operation = "복구 확인 후 종료", Confirmed = true });
                if (!result.Success) throw new InvalidOperationException(result.Message);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (Exception) when (process.HasExited)
            {
                // 종료를 확인했다면 종료 응답을 읽는 중의 경쟁과 무관하게 새 연결이 가능하다.
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("작업용 Revit 종료가 아직 끝나지 않았습니다. 해당 창의 안내를 확인한 뒤 다시 시도하세요.");
            }
        }
        ResetRecovery();
    }

    private void ResetRecovery()
    {
        _process?.Dispose();
        _process = null;
        Connected = NeedsAttention = CanRestartForRead = false;
        _attentionRequest = null;
        _attentionDetail = "";
    }

    public async Task RestartForReadFailure(string revitExe)
    {
        if (!CanRestartForRead || _sending) throw new InvalidOperationException("조회 전용 재연결을 안전하게 진행할 수 없는 상태입니다.");
        if (_process is { HasExited: false })
        {
            Progress?.Invoke("조회 메모리 정리 · 빈 작업용 Revit을 정상 종료하는 중…");
            var result = await Send(new Request { Operation = "종료" });
            if (!result.Success) throw new InvalidOperationException(result.Message);
            var watch = Stopwatch.StartNew();
            while (!_process.HasExited && watch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(500);
            if (!_process.HasExited) throw new InvalidOperationException("작업용 Revit의 종료가 아직 끝나지 않았습니다. 해당 창의 안내를 확인한 뒤 ‘미조회 파일 계속 조회’를 누르세요. 강제 종료하지 않았습니다.");
        }
        ResetRecovery();
        await Connect(revitExe);
    }
}
