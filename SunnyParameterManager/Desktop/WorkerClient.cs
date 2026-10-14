using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using SunnyParameterManager.Core;

namespace SunnyParameterManager.Desktop;

public sealed class WorkerClient
{
    private Process? _process;
    private string _folder = "";
    private string _token = "";
    private bool _sending;
    public bool Connected { get; private set; }
    public bool NeedsAttention { get; private set; }
    public string SessionFolder => _folder;
    public Action<string>? Progress { get; set; }

    public async Task Connect(string revitExe)
    {
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
        Progress?.Invoke("작업용 Revit 시작 중… 로그인·애드인 확인 창이 나타나면 직접 확인해 주세요. 기존 Revit은 건드리지 않습니다.");
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
        NeedsAttention = true;
        throw new TimeoutException("연결 대기 시간이 지났습니다. 작업용 Revit의 시작/로그인/애드인 안내를 확인하세요. 프로세스를 강제 종료하지 않았습니다.");
    }

    public async Task<Response> Send(Request request)
    {
        if (!Connected || _process == null || _process.HasExited || NeedsAttention) throw new InvalidOperationException("작업용 Revit을 먼저 연결하세요. 이전 작업 확인이 필요하면 해당 Revit을 확인한 뒤 다시 시작하세요.");
        if (_sending) throw new InvalidOperationException("진행 중인 작업이 있습니다.");
        _sending = true;
        try
        {
            request.Token = _token;
            Protocol.Write(Path.Combine(_folder, "request.json"), request);
            var resultPath = Path.Combine(_folder, request.Id + ".result.json");
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromMinutes(45))
            {
                if (File.Exists(resultPath))
                {
                    var result = await Task.Run(() => Protocol.Read<Response>(resultPath));
                    if (result.Id != request.Id) throw new InvalidDataException("작업 응답 식별자가 다릅니다.");
                    NeedsAttention |= result.RequiresAttention;
                    result.Snapshot?.ConnectCatalogs();
                    if (request.Operation == "종료") Connected = false;
                    return result;
                }
                if (_process.HasExited) throw new InvalidOperationException("작업용 Revit이 종료되었습니다. 특히 저장/동기화 중이었다면 반영 여부를 확인해야 합니다. 자동 재실행하지 않습니다.");
                var error = Path.Combine(_folder, "worker-error.json");
                if (File.Exists(error)) throw new InvalidOperationException(Protocol.Read<Response>(error).Message);
                var progressPath = Path.Combine(_folder, request.Id + ".progress.json");
                if (File.Exists(progressPath)) Progress?.Invoke(Protocol.Read<Response>(progressPath).Stage + $" · {watch.Elapsed:mm\\:ss}");
                await Task.Delay(750);
            }
            throw new TimeoutException("45분 대기 시간이 지났습니다. 작업용 Revit을 확인하세요. 작업은 강제 취소되지 않았고 자동 재시도하지 않습니다.");
        }
        catch { NeedsAttention = true; throw; }
        finally { _sending = false; }
    }
}
