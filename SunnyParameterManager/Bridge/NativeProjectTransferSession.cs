using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SunnyParameterManager.Core;
using System.Security.Cryptography;
using System.Text;

namespace SunnyParameterManager.Bridge;

// 프로젝트 매개변수의 내용은 직접 복사하지 않는다. Revit 기본 '프로젝트 표준 전송' 명령만 실행한다.
internal sealed class NativeProjectTransferSession(string folder, Action<Response> progress)
{
    private Document? _source;
    private Document? _target;
    private Document? _anchor;
    private Mutex? _gate;
    private bool _locked;
    private bool _transferred;
    private string _startId = "";
    private string _identity = "";
    private string _sourceTitle = "";
    private string _recoveryPath = "";
    private string _backupPath = "";

    public void MarkTransferred(Document source, Document target) =>
        _transferred = _source != null && _target != null &&
            string.Equals(source.PathName, _source.PathName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(target.PathName, _target.PathName, StringComparison.OrdinalIgnoreCase);

    public Response Start(UIApplication ui, Request req, ref string anchorPath)
    {
        var response = new Response { Id = req.Id };
        void Step(string stage) { response.Stage = stage; progress(new Response { Id = req.Id, Stage = stage, RecoveryPath = _recoveryPath, BackupPath = _backupPath }); }
        try
        {
            if (!req.Confirmed || req.TransferSource == null || req.Edits.Count != 0 || req.Query != null)
                throw new InvalidOperationException("기준 RVT와 대상 RVT를 선택한 뒤 전송을 시작하세요.");
            _startId = req.Id;
            _identity = req.Identity;
            var targetPath = ModelEngine.Canonical(req.Path);
            var sourcePath = ModelEngine.Canonical(req.TransferSource.Path);
            if (!string.Equals(targetPath, req.CanonicalPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sourcePath, req.TransferSource.CanonicalPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("조회한 기준·대상 파일 경로가 현재 선택과 다릅니다. 다시 조회하세요.");
            Step("대상 모델 상태 확인");
            using var targetInfo = ModelEngine.ReadFileInfo(targetPath, Step);
            using var sourceInfo = ModelEngine.ReadFileInfo(sourcePath, Step);
            if (!targetInfo.IsSavedInCurrentVersion || !sourceInfo.IsSavedInCurrentVersion || targetInfo.IsInProgress || sourceInfo.IsInProgress)
                throw new InvalidOperationException("기준과 대상은 저장이 끝난 Revit 2026 RVT여야 합니다.");
            var central = targetInfo.IsWorkshared ? ModelEngine.Canonical(targetInfo.CentralPath) : "";
            var isCentral = targetInfo.IsWorkshared && string.Equals(targetPath, central, StringComparison.OrdinalIgnoreCase);
            if (targetInfo.IsWorkshared && !isCentral && !string.Equals(targetInfo.Username, ui.Application.Username, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("다른 사용자의 로컬 파일을 대상으로 열 수 없습니다.");
            if (targetInfo.IsWorkshared && !isCentral && !req.LocalChangesConfirmed)
                throw new InvalidOperationException("선택한 로컬 파일의 미동기화 변경도 함께 반영되는지 확인해 주세요.");
            var lockKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((central.Length > 0 ? central : targetPath).ToUpperInvariant())));
            _gate = new Mutex(false, "Local\\SunnyParameterManager-" + lockKey);
            try { _locked = _gate.WaitOne(0); }
            catch (AbandonedMutexException) { _locked = true; throw new InvalidOperationException("이 모델의 이전 작업이 비정상 종료됐습니다. 상태를 확인해 주세요."); }
            if (!_locked) throw new InvalidOperationException("이 모델은 다른 매개변수 관리자 작업에서 사용 중입니다.");

            Step("작업용 모델과 복구 사본 준비");
            var workFolder = Path.Combine(folder, "NativeTransfer", req.Id);
            Directory.CreateDirectory(workFolder);
            var targetOpenPath = targetPath;
            if (isCentral)
            {
                targetOpenPath = Path.Combine(workFolder, "대상_" + Path.GetFileName(targetPath));
                WorksharingUtils.CreateNewLocal(ModelPathUtils.ConvertUserVisiblePathToModelPath(targetPath),
                    ModelPathUtils.ConvertUserVisiblePathToModelPath(targetOpenPath));
            }
            _backupPath = Path.Combine(workFolder, "변경전_" + Path.GetFileName(targetOpenPath));
            File.Copy(targetOpenPath, _backupPath, false);
            _recoveryPath = targetOpenPath;
            var sourceOpenPath = Path.Combine(workFolder, "기준_" + Path.GetFileName(sourcePath));
            if (sourceInfo.IsWorkshared && string.Equals(sourcePath, ModelEngine.Canonical(sourceInfo.CentralPath), StringComparison.OrdinalIgnoreCase))
                WorksharingUtils.CreateNewLocal(ModelPathUtils.ConvertUserVisiblePathToModelPath(sourcePath),
                    ModelPathUtils.ConvertUserVisiblePathToModelPath(sourceOpenPath));
            else File.Copy(sourcePath, sourceOpenPath, false);

            _anchor = EnsureAnchor(ui, ref anchorPath);
            Step("기준과 대상 모델을 Revit에 열기");
            _source = ui.OpenAndActivateDocument(sourceOpenPath).Document;
            _sourceTitle = _source.Title + ".rvt";
            _target = ui.OpenAndActivateDocument(targetOpenPath).Document;
            if (_target.IsFamilyDocument || _target.IsReadOnly || _target.IsDetached || _target.IsLinked ||
                _target.IsWorkshared != targetInfo.IsWorkshared)
                throw new InvalidOperationException("대상 RVT를 수정 가능한 프로젝트로 열지 못했습니다.");
            var actualIdentity = "파일-v2/" + _target.CreationGUID.ToString("D") + "/" +
                (_target.IsWorkshared ? "중앙:" + central.ToUpperInvariant() : "일반");
            if (actualIdentity != _identity) throw new InvalidOperationException("조회한 대상과 다른 모델이 열렸습니다. 다시 조회하세요.");
            var command = RevitCommandId.LookupPostableCommandId(PostableCommand.TransferProjectStandards);
            if (!ui.CanPostCommand(command)) throw new InvalidOperationException("Revit 기본 프로젝트 표준 전송 명령을 시작할 수 없습니다.");
            Step("Revit 기본 프로젝트 표준 전송 창 열기");
            ui.PostCommand(command);
            response.Success = true;
            response.Stage = "기본 전송 창 요청";
            response.Message = _sourceTitle;
            response.RecoveryPath = _recoveryPath;
            response.BackupPath = _backupPath;
        }
        catch (Exception ex)
        {
            bool clean = CloseOpenDocuments(ui);
            ReleaseGate();
            response.Stage = clean ? "기본 전송 시작 실패" : "작업용 Revit 확인 필요";
            response.Message = ex.Message;
            response.RequiresAttention = !clean;
            response.CanContinueAfterPreflightFailure = clean;
            response.RecoveryPath = _recoveryPath;
            response.BackupPath = _backupPath;
        }
        return response;
    }

    public Response Complete(UIApplication ui, Request req)
    {
        var response = new Response { Id = req.Id, RecoveryPath = _recoveryPath, BackupPath = _backupPath };
        void Step(string stage) { response.Stage = stage; progress(new Response { Id = req.Id, Stage = stage, RecoveryPath = _recoveryPath, BackupPath = _backupPath }); }
        try
        {
            if (req.Identity != _identity || req.TransferSource?.Path == null || _target == null || _source == null || _anchor == null)
                throw new InvalidOperationException("시작한 전송과 완료 요청이 일치하지 않습니다.");
            if (!_transferred)
            {
                if (!CloseOpenDocuments(ui)) throw new InvalidOperationException("취소된 전송의 작업용 문서를 닫지 못했습니다.");
                response.Stage = "전송 취소";
                response.Message = "Revit 기본 전송이 완료되지 않아 대상 파일을 저장하지 않았습니다.";
                ReleaseGate();
                return response;
            }
            Step("Revit 기본 전송 후 로컬 저장");
            _target.Save();
            if (_target.IsWorkshared)
            {
                Step("중앙 동기화");
                using var sync = new SynchronizeWithCentralOptions { SaveLocalBefore = true, SaveLocalAfter = true, Comment = "Sunny Parameter Manager " + _startId };
                using var relinquish = new RelinquishOptions(true);
                sync.SetRelinquishOptions(relinquish);
                using var options = new TransactWithCentralOptions();
                options.SetLockCallback(new NoWait());
                _target.SynchronizeWithCentral(options, sync);
                response.CentralSynchronized = true;
                OwnershipRelease.Complete(_target, Step);
                response.OwnershipReleased = true;
            }
            Step("작업용 모델 닫기");
            if (!CloseOpenDocuments(ui)) throw new InvalidOperationException("전송 후 작업용 문서를 닫지 못했습니다.");
            ReleaseGate();
            response.Success = true;
            response.Applied = 1;
            response.Stage = "완료";
            response.Message = "Revit 기본 프로젝트 표준 전송에서 프로젝트 매개변수만 전송했습니다. " +
                (response.CentralSynchronized ? "중앙 동기화와 소유권 반환을 마쳤습니다." : "대상 RVT를 저장했습니다.");
        }
        catch (Exception ex)
        {
            response.RequiresAttention = true;
            response.Stage = "전송·저장 상태 확인 필요";
            response.Message = ex.Message + "\n작업용 Revit과 복구 사본을 보존했습니다. 같은 변경을 자동 재실행하지 않습니다.";
        }
        return response;
    }

    private Document EnsureAnchor(UIApplication ui, ref string anchorPath)
    {
        var knownAnchorPath = anchorPath;
        if (knownAnchorPath.Length > 0)
        {
            var existing = ui.Application.Documents.Cast<Document>().FirstOrDefault(d => string.Equals(d.PathName, knownAnchorPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;
        }
        var path = Path.Combine(folder, "작업용_빈모델.rvt");
        if (!File.Exists(path))
        {
            var blank = ui.Application.NewProjectDocument(UnitSystem.Metric);
            try
            {
                using var options = new SaveAsOptions { OverwriteExistingFile = true };
                blank.SaveAs(path, options);
            }
            finally { blank.Close(false); }
        }
        anchorPath = path;
        return ui.OpenAndActivateDocument(path).Document;
    }

    private bool CloseOpenDocuments(UIApplication ui)
    {
        try
        {
            if (_anchor == null) return _source == null && _target == null;
            ui.OpenAndActivateDocument(_anchor.PathName);
            if (_target != null) { if (!_target.Close(false)) return false; _target = null; }
            if (_source != null) { if (!_source.Close(false)) return false; _source = null; }
            return true;
        }
        catch { return false; }
    }

    private void ReleaseGate()
    {
        if (_locked) { _gate?.ReleaseMutex(); _locked = false; }
        _gate?.Dispose(); _gate = null;
    }

    private sealed class NoWait : ICentralLockedCallback { public bool ShouldWaitForLockAvailability() => false; }
}
