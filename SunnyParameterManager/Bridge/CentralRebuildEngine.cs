using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using System.Security.Cryptography;
using System.Text;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace SunnyParameterManager.Bridge;

// 기존 중앙과 연결을 끊고 같은 파일 경로에 새로운 중앙을 생성한다.
// SaveAs 이후에는 결과가 불명확할 수 있으므로 문서를 임의로 닫거나 원본 백업을 자동 복원하지 않는다.
internal sealed class CentralRebuildEngine(string folder, Action<Response> progress)
{
    internal Response Run(RevitApplication app, Request req)
    {
        var result = new Response { Id = req.Id };
        Document? doc = null;
        Mutex? gate = null;
        bool locked = false, saveStarted = false;
        void Step(string stage)
        {
            result.Stage = stage;
            progress(new Response { Id = result.Id, Stage = stage, RecoveryPath = result.RecoveryPath,
                BackupPath = result.BackupPath, CentralSynchronized = result.CentralSynchronized,
                OwnershipReleased = result.OwnershipReleased });
        }
        try
        {
            if (req.Operation != "중앙 재생성" || !req.Confirmed || req.CentralRebuild?.FormatVersion != 2 ||
                req.Edits.Count != 0 || req.Query != null || req.LinkRemoval != null || req.CleanupRules != null)
                throw new InvalidOperationException("중앙 재생성 검토 정보가 없거나 다른 변경이 섞였습니다. 다시 조회·검토하세요.");
            var source = ModelEngine.Canonical(req.Path);
            if (!source.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(source, req.CanonicalPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("조회한 RVT 경로가 달라졌습니다. 다시 조회하세요.");
            var lockKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToUpperInvariant())));
            gate = new Mutex(false, "Local\\SunnyParameterManager-" + lockKey);
            try { locked = gate.WaitOne(0); }
            catch (AbandonedMutexException) { locked = true; throw new InvalidOperationException("이 중앙의 이전 작업이 비정상 종료됐습니다. 복구 기록을 확인하세요."); }
            if (!locked) throw new InvalidOperationException("이 중앙파일은 다른 매개변수 관리자 작업에서 사용 중입니다.");

            Step("선택한 RVT 형식·경로 재확인");
            string sourceKind;
            using (var info = ModelEngine.ReadFileInfo(source, Step))
            {
                if (!info.IsSavedInCurrentVersion || info.IsInProgress)
                    throw new InvalidOperationException("선택 파일은 저장 중이지 않은 Revit 2026 RVT여야 합니다.");
                sourceKind = ModelEngine.CentralRebuildSourceKind(source, info);
                if (sourceKind != req.CentralRebuild.SourceKind)
                    throw new InvalidOperationException("조회 이후 파일의 중앙·작업공유 상태가 달라졌습니다. 다시 조회하세요.");
            }
            Step("조회 후 원본 변경 확인");
            CentralRebuildPlanner.RequireUnchanged(req.CentralRebuild, CentralRebuildPlanner.Capture(source, sourceKind));
            if (sourceKind == "중앙")
            {
                Step("기존 중앙 작업세트 소유자 확인");
                var previews = WorksharingUtils.GetUserWorksetInfo(ModelPathUtils.ConvertUserVisiblePathToModelPath(source));
                try
                {
                    var owned = previews.Where(w => !string.IsNullOrWhiteSpace(w.Owner)).Take(12)
                        .Select(w => $"{w.Name}: {w.Owner}").ToList();
                    if (owned.Count > 0)
                        throw new InvalidOperationException("기존 중앙에 소유자가 남은 작업세트가 있습니다. 해당 작업자의 동기화·소유권 반환을 확인한 뒤 다시 조회하세요.\n" + string.Join("\n", owned));
                }
                finally { foreach (var preview in previews) preview.Dispose(); }
            }

            Step("선택한 RVT 복구 사본 저장");
            var backupDir = Path.Combine(folder, "Backups", req.Id);
            Directory.CreateDirectory(backupDir);
            result.BackupPath = Path.Combine(backupDir, Path.GetFileName(source));
            File.Copy(source, result.BackupPath, false);
            CentralRebuildPlanner.RequireBackupContents(req.CentralRebuild, CentralRebuildPlanner.Capture(result.BackupPath, sourceKind));
            CentralRebuildPlanner.RequireUnchanged(req.CentralRebuild, CentralRebuildPlanner.Capture(source, sourceKind));

            Step("선택한 RVT에서 분리 · 작업세트 유지");
            using var open = new OpenOptions { DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets };
            open.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
            doc = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(source), open);
            if (doc.IsFamilyDocument || doc.IsLinked || doc.IsReadOnly ||
                !doc.IsWorkshared || !doc.IsDetached ||
                !string.Equals(CentralRebuildPlanner.Identity(doc.CreationGUID, source), req.Identity, StringComparison.Ordinal))
                throw new InvalidOperationException("조회한 모델을 새 중앙 생성 가능 상태로 열지 못했습니다. 원본은 덮어쓰지 않았습니다.");
            result.RecoveryPath = source;
            CentralRebuildPlanner.RequireUnchanged(req.CentralRebuild, CentralRebuildPlanner.Capture(source, sourceKind));


            // SaveAsCentral은 분리 문서에 필수. 이 시점부터 저장 결과가 불명확할 수 있다.
            Step("원본 파일명으로 새 중앙 저장 중 — 중단 시 결과 확인 필요");
            saveStarted = true;
            using var save = new SaveAsOptions { OverwriteExistingFile = true };
            using var worksharing = new WorksharingSaveAsOptions { SaveAsCentral = true };
            save.SetWorksharingOptions(worksharing);
            doc.SaveAs(ModelPathUtils.ConvertUserVisiblePathToModelPath(source), save);
            if (!doc.IsWorkshared || doc.IsDetached ||
                !string.Equals(ModelEngine.Canonical(ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath())), source, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("새 중앙 저장 후 중앙 연결을 확인할 수 없습니다. 복구 사본과 현재 문서를 보존했습니다.");

            Step("새 중앙 동기화 중 — 중단 시 결과 확인 필요");
            using var sync = new SynchronizeWithCentralOptions { SaveLocalBefore = true, SaveLocalAfter = true,
                Comment = "Sunny Parameter Manager · 중앙 재생성 " + req.Id };
            using var relinquish = new RelinquishOptions(true);
            sync.SetRelinquishOptions(relinquish);
            using var transact = new TransactWithCentralOptions();
            transact.SetLockCallback(new NoWait());
            doc.SynchronizeWithCentral(transact, sync);
            result.CentralSynchronized = true;
            OwnershipRelease.Complete(doc, Step);
            result.OwnershipReleased = true;
            Step("최종 저장");
            doc.Save();
            Step("문서 닫기");
            if (!doc.Close(false)) throw new InvalidOperationException("새 중앙의 작업은 끝났지만 문서를 닫지 못했습니다.");
            doc = null;
            result.Success = true;
            result.Stage = "완료";
            result.Message = "작업세트를 유지한 새 중앙을 선택한 RVT 경로에 저장하고 동기화·소유권 반환·저장·닫기를 완료했습니다." +
                (sourceKind == "작업공유 사본" ? " 이전 중앙 경로의 파일은 변경하지 않았습니다." : " 기존 작업자의 로컬은 새로 만들어야 합니다.");
        }
        catch (Exception ex)
        {
            result.RequiresAttention = saveStarted;
            result.Stage = saveStarted ? "중앙 생성 결과 확인 필요" : "실패 — 선택 파일 미교체";
            result.Message = "[" + result.Stage + "] " + ex.Message +
                (saveStarted ? "\n새 중앙과 기존 중앙 중 어느 상태인지, 백업 및 소유권을 확인하세요. 자동 재시도하거나 백업으로 자동 복원하지 않습니다." : "\n새 중앙 저장 전 중단되었습니다. 원본 파일은 덮어쓰지 않았습니다.");
            if (!saveStarted && doc != null)
            {
                try { if (!doc.Close(false)) result.RequiresAttention = true; }
                catch { result.RequiresAttention = true; }
            }
        }
        finally { if (locked) gate?.ReleaseMutex(); gate?.Dispose(); }
        return result;
    }

    private sealed class NoWait : ICentralLockedCallback { public bool ShouldWaitForLockAvailability() => false; }
}
