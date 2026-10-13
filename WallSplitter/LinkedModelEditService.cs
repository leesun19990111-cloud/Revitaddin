using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // 링크 문서(GetLinkDocument)는 절대 수정하지 않는다. 사용자가 선택한 중앙모델의
    // 새 로컬만 편집하고, 확인한 작업을 동기화한 다음 호스트 링크 상태를 복원한다.
    internal sealed class LinkedEditPlan
    {
        internal string Summary = "";
        internal Func<Document, List<ChangeLogEntry>, Result> Apply = null!;
    }

    internal sealed class LinkedEditReport
    {
        internal bool Synced, HasProblems;
        internal readonly List<string> Messages = new();
    }

    internal static class LinkedModelEditService
    {
        internal static bool Choose(UIApplication app, string title, out RevitLinkType? link)
        {
            link = null;
            Document host = app.ActiveUIDocument.Document;
            var links = new FilteredElementCollector(host).OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>().Where(t => !t.IsNestedLink).Cast<Element>().ToList();
            if (links.Count == 0) return true;
            var dialog = new TaskDialog(title)
            {
                MainInstruction = "편집할 파일을 선택하세요.",
                MainContent = "링크 중앙모델은 한 파일씩 별도 창에서 편집합니다.\n현재 프로젝트와 링크의 요소·재료는 섞이지 않습니다.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "현재 프로젝트 — " + host.Title);
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "링크된 중앙모델 원본 편집");
            TaskDialogResult choice = dialog.Show();
            if (choice == TaskDialogResult.CommandLink1) return true;
            if (choice != TaskDialogResult.CommandLink2) return false;
            if (NamerWindow.Instance != null)
            {
                TaskDialog.Show(title, "열려 있는 NAMER의 작업을 완료하거나 닫은 뒤 링크 편집을 실행해 주세요.");
                return false;
            }
            var picker = new ElementPickerWindow(
                "편집할 링크 파일 하나를 선택하세요. 공유폴더의 중앙 RVT만 지원합니다.\n중첩 링크·클라우드·Revit Server 모델은 제외됩니다.",
                links, e => e.Name + " — " + DisplayPath((RevitLinkType)e));
            picker.Title = title + " — 링크 중앙모델 선택";
            new WindowInteropHelper(picker) { Owner = app.MainWindowHandle };
            if (picker.ShowDialog() != true || picker.Result is not RevitLinkType selected) return false;
            link = selected;
            return true;
        }

        private static string DisplayPath(RevitLinkType link)
        {
            try { return ModelPathUtils.ConvertModelPathToUserVisiblePath(link.GetExternalFileReference().GetAbsolutePath()); }
            catch { return "파일 경로 확인 불가"; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

        // 매핑 드라이브와 UNC/심볼릭 링크가 같은 파일을 가리키는 경우에도 같은 경로로 비교한다.
        // 읽기 핸들만 사용하며 파일 내용은 수정하지 않는다. 정규화 실패는 안전하게 중단한다.
        internal static string CanonicalPath(string path)
        {
            using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var buffer = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
                throw new IOException("파일 경로를 확인할 수 없습니다: " + path);
            return buffer.ToString().TrimEnd('\\');
        }

        internal static bool SamePath(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return string.Equals(CanonicalPath(a), CanonicalPath(b),
                StringComparison.OrdinalIgnoreCase);
        }

        internal static void ValidateHostLocal(Document host)
        {
            if (host.IsDetached) throw new InvalidOperationException("중앙에서 분리한 문서에서는 링크 원본 편집을 시작할 수 없습니다.");
            if (string.IsNullOrWhiteSpace(host.PathName))
                throw new InvalidOperationException("현재 프로젝트의 로컬 파일을 먼저 저장해 주세요.");
            ModelPath central = host.GetWorksharingCentralModelPath();
            if (central is not FilePath)
                throw new InvalidOperationException("현재 프로젝트도 파일 기반 중앙모델의 로컬이어야 합니다.");
            string path = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
            if (SamePath(host.PathName, path))
                throw new InvalidOperationException("현재 프로젝트가 중앙 원본 경로에서 열려 있습니다. 새 로컬로 열어 주세요.\n현재: " + host.PathName + "\n중앙: " + path);
            // 새 로컬은 첫 저장 전 BasicFileInfo.IsLocal=false일 수 있다. 이 값만으로 차단하지 않는다.
        }

        internal static bool Confirm(string title, string instruction, string content)
        {
            return new TaskDialog(title)
            {
                MainInstruction = instruction, MainContent = content,
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            }.Show() == TaskDialogResult.Yes;
        }

        // 잠금 충돌을 무한 재시도하지 않는다. 다른 사용자의 소유권을 강제로 가져오지 않는다.
        private sealed class NoLockWait : ICentralLockedCallback
        {
            public bool ShouldWaitForLockAvailability() => false;
        }

        internal static Result Run(UIApplication app, RevitLinkType link, string title,
            Func<Document, LinkedEditPlan?> prepare, LinkedEditReport? batch = null)
        {
            Document host = app.ActiveUIDocument.Document;
            Document? local = null;
            bool restore = false, localUnload = false, committed = false, saved = false, synced = false;
            string localPath = "", centralPath = "";
            var messages = new List<string>();
            var logs = new List<ChangeLogEntry>();
            try
            {
                if (host.IsReadOnly || host.IsModifiable || host.IsFamilyDocument || host.IsLinked)
                    throw new InvalidOperationException("현재 프로젝트에서 링크 편집을 시작할 수 없습니다. 편집 모드를 종료해 주세요.");
                if (link.IsNestedLink || !RevitLinkType.IsLoaded(host, link.Id) || link.LocallyUnloaded)
                    throw new InvalidOperationException("로드된 최상위 링크만 편집할 수 있습니다. 먼저 링크를 로드해 주세요.");
                if (!link.IsNotLoadedIntoMultipleOpenDocuments())
                    throw new InvalidOperationException("이 링크가 다른 열린 프로젝트에도 로드되어 있습니다. 다른 프로젝트를 닫은 후 실행해 주세요.");
                ModelPath path = link.GetExternalFileReference().GetAbsolutePath();
                if (path is not FilePath)
                    throw new InvalidOperationException("공유폴더의 중앙 RVT만 지원합니다. ACC/BIM 360 및 Revit Server는 지원하지 않습니다.");
                centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
                if (!File.Exists(centralPath))
                    throw new InvalidOperationException("링크 원본 파일에 접근할 수 없습니다. 서버 연결과 권한을 확인해 주세요.");
                using (BasicFileInfo info = BasicFileInfo.Extract(centralPath))
                {
                    if (!info.IsWorkshared || !info.IsCentral)
                        throw new InvalidOperationException("선택한 링크 경로가 중앙모델이 아닙니다. 개인 로컬 파일을 원본으로 대신 수정하지 않습니다.");
                    if (!info.IsSavedInCurrentVersion)
                        throw new InvalidOperationException("현재 Revit 버전으로 저장된 중앙모델만 지원합니다. 자동 업그레이드는 하지 않습니다.");
                }
                if (host.IsWorkshared)
                {
                    ValidateHostLocal(host);
                    localUnload = true;
                }
                foreach (Document open in app.Application.Documents)
                {
                    if (open.IsLinked || open.IsFamilyDocument) continue;
                    if (SamePath(open.PathName, centralPath) ||
                        (open.IsWorkshared && SamePath(ModelPathUtils.ConvertModelPathToUserVisiblePath(
                            open.GetWorksharingCentralModelPath()), centralPath)))
                        throw new InvalidOperationException("같은 중앙모델 또는 로컬이 이미 열려 있습니다. 그 문서에서 직접 편집하거나 닫은 후 실행해 주세요.");
                }
                if (batch == null && !Confirm(title, "링크 중앙모델 편집을 시작할까요?",
                    centralPath + "\n\n전용 로컬을 만들고 편집 중 링크를 잠시 내립니다." +
                    (localUnload ? " 현재 사용자에게만 해제합니다." : "") +
                    "\n링크 해제·갱신 과정에서 현재 프로젝트의 실행 취소 기록이 지워질 수 있습니다. 필요한 작업을 먼저 저장해 주세요." +
                    "\n공유좌표는 저장하지 않습니다. 창을 닫은 뒤 별도 확인을 거쳐 중앙모델에 동기화합니다." +
                    "\n원본을 쓰는 다른 프로젝트에도 변경이 반영됩니다. 링크 편집 창에서는 다른 Revit 작업을 병행할 수 없습니다."))
                    return Result.Cancelled;

                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WallSplitter", "LinkedModelEdits", DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                localPath = Path.Combine(folder, Path.GetFileName(centralPath));
                WorksharingUtils.CreateNewLocal(path, ModelPathUtils.ConvertUserVisiblePathToModelPath(localPath));
                // 해제 중 예외가 나도 실제 상태를 다시 검사하여 복원을 시도한다.
                restore = true;
                if (localUnload) link.UnloadLocally(null); else link.Unload(null);
                if (RevitLinkType.IsLoaded(host, link.Id))
                    throw new InvalidOperationException("링크를 해제하지 못하여 원본 편집을 중단했습니다.");
                using (var options = new OpenOptions())
                {
                    options.DetachFromCentralOption = DetachFromCentralOption.DoNotDetach;
                    using (var worksets = new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets))
                        options.SetOpenWorksetsConfiguration(worksets);
                    local = app.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(localPath), options);
                }
                if (local.IsLinked || local.IsReadOnly || !local.IsWorkshared ||
                    !SamePath(ModelPathUtils.ConvertModelPathToUserVisiblePath(local.GetWorksharingCentralModelPath()), centralPath))
                    throw new InvalidOperationException("새 로컬의 중앙모델 연결을 확인하지 못했습니다. 변경하지 않습니다.");

                LinkedEditPlan? plan = prepare(local);
                if (plan == null) return Result.Cancelled;
                if (batch == null && !Confirm(title, "다음 변경을 중앙모델에 동기화할까요?",
                    centralPath + "\n\n" + plan.Summary +
                    "\n\n다른 사용자가 잠근 요소는 강제로 수정하지 않습니다. 한 항목이라도 실패하면 이번 파일의 변경 전체를 취소합니다." +
                    "\n중앙모델 동기화 후에는 현재 프로젝트의 실행 취소로 되돌릴 수 없습니다."))
                    return Result.Cancelled;
                using (var group = new TransactionGroup(local, title + " 링크 원본 편집"))
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("편집을 시작하지 못했습니다.");
                    Result edited = plan.Apply(local, logs);
                    if (edited != Result.Succeeded || logs.Count == 0)
                    {
                        group.RollBack();
                        messages.Add("반영 가능한 변경이 없거나 일부 항목이 실패하여 이번 파일의 변경 전체를 취소했습니다.");
                        return Result.Cancelled;
                    }
                    if (group.Assimilate() != TransactionStatus.Committed)
                        throw new InvalidOperationException("변경 묶음을 확정하지 못했습니다.");
                    committed = true;
                }
                // 네트워크 오류가 나도 사용자가 직접 복구할 수 있도록 먼저 전용 로컬에 저장한다.
                local.Save();
                saved = true;
                using (var transact = new TransactWithCentralOptions())
                using (var sync = new SynchronizeWithCentralOptions())
                using (var relinquish = new RelinquishOptions(true))
                {
                    transact.SetLockCallback(new NoLockWait());
                    sync.SetRelinquishOptions(relinquish);
                    sync.SaveLocalBefore = true;
                    sync.SaveLocalAfter = true;
                    sync.Comment = "Sunny Tools " + title + " — 링크 원본 편집";
                    local.SynchronizeWithCentral(transact, sync);
                }
                synced = true;
                foreach (ChangeLogEntry entry in logs) entry.SourceDocumentTitle = Path.GetFileNameWithoutExtension(centralPath);
                ChangeLog.Append(logs);
                messages.Add("중앙모델 동기화를 완료했습니다. 변경 기록 " + logs.Count + "건.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                messages.Add((synced ? "중앙 동기화 이후 처리 오류: " : "작업을 완료하지 못했습니다: ") + ex.GetBaseException().Message);
                return Result.Cancelled;
            }
            finally
            {
                if (local != null && local.IsValidObject)
                {
                    if (committed && !saved && !synced)
                    {
                        try { local.Save(); saved = true; }
                        catch (Exception ex) { messages.Add("복구 로컬 저장도 실패했습니다. 문서를 닫지 않고 유지합니다: " + ex.Message); }
                    }
                    if (!committed)
                    {
                        try
                        {
                            using var relinquish = new RelinquishOptions(true);
                            using var transact = new TransactWithCentralOptions();
                            transact.SetLockCallback(new NoLockWait());
                            WorksharingUtils.RelinquishOwnership(local, relinquish, transact);
                        }
                        catch (Exception ex) { messages.Add("빌린 요소의 소유권 반환을 확인하지 못했습니다: " + ex.Message); }
                    }
                    if (!committed || saved || synced)
                    {
                        try { if (!local.Close(false)) messages.Add("전용 로컬을 닫지 못했습니다."); }
                        catch (Exception ex) { messages.Add("전용 로컬 닫기 실패: " + ex.Message); }
                    }
                }
                if (committed && !synced)
                    messages.Add("중앙 반영 완료 여부를 확인하지 못했습니다. 자동 재시도하지 마세요.\n중앙모델을 확인한 후 복구 로컬을 열어 비교·동기화해 주세요:\n" + localPath);
                if (restore && host.IsValidObject && link.IsValidObject)
                {
                    try
                    {
                        if (localUnload)
                        {
                            if (link.LocallyUnloaded) link.RevertLocalUnloadStatus();
                        }
                        else if (!RevitLinkType.IsLoaded(host, link.Id)) link.Load();
                        if (!RevitLinkType.IsLoaded(host, link.Id))
                            messages.Add("링크가 로드되지 않았습니다. 링크 관리에서 상태를 확인해 주세요.");
                    }
                    catch (Exception ex) { messages.Add("링크 복원 실패 — 링크 관리에서 다시 로드해 주세요: " + ex.Message); }
                }
                if (batch != null)
                {
                    batch.Synced = synced;
                    batch.HasProblems = !synced || messages.Count != 1;
                    batch.Messages.AddRange(messages);
                    if (messages.Count == 0) batch.Messages.Add("미처리 — 변경 없음 또는 취소");
                }
                else if (messages.Count > 0) TaskDialog.Show(title + " — 링크 편집 결과", string.Join("\n\n", messages));
            }
        }
    }
}
