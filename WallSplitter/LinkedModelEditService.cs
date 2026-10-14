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
    // 같은 Revit에 이미 열린 원본/로컬만 사용한다. 자동 해제·재열기는 금지한다.
    internal sealed class LinkedEditPlan
    {
        internal string Summary = "";
        internal Func<Document, List<ChangeLogEntry>, Result> Apply = null!;
    }

    internal sealed class LinkedEditReport
    {
        internal bool Synced, HasProblems;
        internal bool RetainChanges;
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

        // Revit 조회 API는 파일 경로도 기본 ModelPath 래퍼로 반환할 수 있다.
        // FilePath로의 형 변환 여부 대신 API의 경로 속성으로 판별한다.
        internal static bool IsFileBasedPath(ModelPath? path) =>
            path != null && !path.Empty && !path.ServerPath && !path.CloudPath;

        // 같은 Revit 세션의 편집 가능한 문서만 찾는다. 링크의 읽기 전용 문서는 대상이 아니다.
        internal static Document? FindOpenTarget(UIApplication app, string centralPath)
        {
            Document? target = null;
            foreach (Document open in app.Application.Documents)
            {
                if (!open.IsValidObject || open.IsLinked || open.IsFamilyDocument) continue;
                bool matches = SamePath(open.PathName, centralPath);
                if (!matches && open.IsWorkshared)
                {
                    ModelPath central = open.GetWorksharingCentralModelPath();
                    matches = IsFileBasedPath(central) && SamePath(
                        ModelPathUtils.ConvertModelPathToUserVisiblePath(central), centralPath);
                }
                if (!matches) continue;
                if (target != null)
                    throw new InvalidOperationException("같은 중앙모델의 편집 문서가 여러 개 열려 있어 대상을 확정할 수 없습니다.");
                target = open;
            }
            if (target != null)
            {
                ValidateOpenTarget(target, centralPath);
                if (SamePath(target.PathName, app.ActiveUIDocument.Document.PathName))
                    throw new InvalidOperationException("링크 원본이 현재 모델과 같습니다. 현재 모델 항목에서 편집해 주세요.");
            }
            return target;
        }

        private static void ValidateOpenTarget(Document target, string centralPath)
        {
            if (!target.IsValidObject || target.IsLinked || target.IsFamilyDocument || target.IsReadOnly ||
                target.IsModifiable || target.IsDetached || !target.IsWorkshared)
                throw new InvalidOperationException("열린 대상 모델을 편집·동기화할 수 없습니다. 읽기 전용/편집 모드/중앙 분리 여부를 확인해 주세요.");
            ModelPath central = target.GetWorksharingCentralModelPath();
            if (string.IsNullOrWhiteSpace(target.PathName) || !IsFileBasedPath(central) ||
                !SamePath(ModelPathUtils.ConvertModelPathToUserVisiblePath(central), centralPath))
                throw new InvalidOperationException("열린 대상 문서의 중앙 연결을 확인하지 못했습니다. 변경하지 않습니다.");
        }

        internal static void ValidateHostLocal(Document host)
        {
            if (host.IsDetached) throw new InvalidOperationException("중앙에서 분리한 문서에서는 링크 원본 편집을 시작할 수 없습니다.");
            if (string.IsNullOrWhiteSpace(host.PathName))
                throw new InvalidOperationException("현재 프로젝트의 로컬 파일을 먼저 저장해 주세요.");
            ModelPath central = host.GetWorksharingCentralModelPath();
            if (!IsFileBasedPath(central))
                throw new InvalidOperationException("현재 프로젝트의 중앙 연결이 비어 있거나 지원하지 않는 서버/클라우드 경로입니다.\n현재: " + host.PathName);
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

        // 조회 전용 사전 검사. 새 로컬 생성/문서 열기/링크 해제는 절대 하지 않는다.
        internal static Document RequireOpenTarget(UIApplication app, RevitLinkType link, string? expectedPath = null)
        {
            Document host = app.ActiveUIDocument.Document;
            if (host.IsReadOnly || host.IsModifiable || host.IsFamilyDocument || host.IsLinked)
                throw new InvalidOperationException("현재 프로젝트에서 링크 편집을 시작할 수 없습니다. 편집 모드를 종료해 주세요.");
            if (!link.IsValidObject || link.IsNestedLink || !RevitLinkType.IsLoaded(host, link.Id) || link.LocallyUnloaded)
                throw new InvalidOperationException("로드된 최상위 링크만 편집할 수 있습니다. 먼저 링크를 로드해 주세요.");
            ModelPath path = link.GetExternalFileReference().GetAbsolutePath();
            if (!IsFileBasedPath(path))
                throw new InvalidOperationException("공유폴더의 중앙 RVT만 지원합니다. ACC/BIM 360 및 Revit Server는 지원하지 않습니다.");
            string centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
            if (!File.Exists(centralPath))
                throw new InvalidOperationException("링크 원본 파일에 접근할 수 없습니다. 서버 연결과 권한을 확인해 주세요.");
            if (expectedPath != null && !SamePath(centralPath, expectedPath))
                throw new InvalidOperationException("링크 원본 경로가 변경되었습니다. 다시 조회해 주세요.");
            using (BasicFileInfo info = BasicFileInfo.Extract(centralPath))
            {
                if (!info.IsWorkshared || !info.IsCentral)
                    throw new InvalidOperationException("선택한 링크 경로가 중앙모델이 아닙니다. 개인 로컬 파일을 원본으로 대신 수정하지 않습니다.");
                if (!info.IsSavedInCurrentVersion)
                    throw new InvalidOperationException("현재 Revit 버전으로 저장된 중앙모델만 지원합니다. 자동 업그레이드는 하지 않습니다.");
            }
            return FindOpenTarget(app, centralPath) ?? throw new InvalidOperationException(
                "편집할 링크 원본의 로컬을 같은 Revit에 먼저 열어 주세요.\n중앙모델: " + centralPath +
                "\nRevit의 파일 열기에서 새 로컬을 생성해 연 뒤, 현재 프로젝트로 돌아와 다시 실행하세요." +
                "\n강제 종료 방지를 위해 Sunny Tools가 링크를 내리거나 임시 로컬을 자동으로 열지 않습니다.");
        }

        private static int _running;

        // 네이티브 강제 종료는 catch로 복구할 수 없다. 마지막 단계를 로컬 파일에 즉시 남긴다.
        // 사용자 경로/모델명/매개변수 값은 기록하지 않는다. 로그 실패가 편집에 영향을 주면 안 된다.
        private static void Trace(string operation, string phase)
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WallSplitter", "Diagnostics");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "LinkedEdit-" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                    DateTime.Now.ToString("O") + " " + operation + " " + phase + Environment.NewLine);
            }
            catch { }
        }

        internal static Result Run(UIApplication app, RevitLinkType link, string title,
            Func<Document, LinkedEditPlan?> prepare, LinkedEditReport? batch = null)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                const string reason = "다른 링크 편집·동기화가 진행 중입니다. 완료된 후 다시 실행해 주세요.";
                if (batch != null) { batch.HasProblems = true; batch.Messages.Add(reason); }
                else TaskDialog.Show(title, reason);
                return Result.Cancelled;
            }
            Document? local = null;
            bool committed = false, synced = false;
            string localPath = "", centralPath = "", operation = Guid.NewGuid().ToString("N");
            var messages = new List<string>();
            var logs = new List<ChangeLogEntry>();
            Trace(operation, "preflight-start");
            try
            {
                local = RequireOpenTarget(app, link);
                localPath = local.PathName;
                centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(link.GetExternalFileReference().GetAbsolutePath());
                bool openedCentral = SamePath(localPath, centralPath);
                LinkedEditPlan? plan = prepare(local);
                if (plan == null) return Result.Cancelled;
                // 통합 일괄 확인과 별개로 기존 작업까지 동기화되는 문서는 반드시 재확인한다.
                if (!Confirm(title, "이미 열린 문서의 모든 미동기화 변경도 함께 반영할까요?",
                    "열린 문서: " + local.Title + "\n" + localPath + "\n중앙모델: " + centralPath +
                    "\n\n" + plan.Summary +
                    "\n\n이번 변경뿐 아니라 이 문서에 이미 있던 미저장·미동기화 작업도 중앙모델에 함께 반영됩니다. 이번 변경만 골라 동기화할 수 없습니다." +
                    (openedCentral ? "\n중앙 원본을 직접 연 문서입니다. 별도 복사/덮어쓰기 없이 Revit의 중앙 동기화를 실행합니다." : "") +
                    "\n문서는 닫지 않으며 기존 요소·작업세트 소유권은 자동 반납하지 않습니다." +
                    "\n링크는 해제/재로드하지 않습니다. 화면에 남은 링크는 필요할 때 링크 관리에서 갱신해 주세요." +
                    "\n적용 중 항목 실패는 이번 변경만 취소합니다. 동기화 실패 시 문서를 유지하며 나머지 모델 처리를 중단합니다." +
                    "\n동기화 완료된 변경은 현재 프로젝트의 실행 취소로 되돌릴 수 없습니다."))
                    return Result.Cancelled;
                RequireOpenTarget(app, link, centralPath);
                ValidateOpenTarget(local, centralPath);
                Trace(operation, "apply-start");
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
                // 모든 트랜잭션과 그룹 종료 뒤에만 중앙 동기화한다.
                Trace(operation, "apply-committed");
                ValidateOpenTarget(local, centralPath);
                using (var transact = new TransactWithCentralOptions())
                using (var sync = new SynchronizeWithCentralOptions())
                using (var relinquish = new RelinquishOptions(false))
                {
                    transact.SetLockCallback(new NoLockWait());
                    sync.SetRelinquishOptions(relinquish);
                    sync.SaveLocalBefore = !openedCentral;
                    sync.SaveLocalAfter = !openedCentral;
                    sync.Comment = "Sunny Tools " + title + " — 링크 원본 편집";
                    Trace(operation, "sync-start");
                    local.SynchronizeWithCentral(transact, sync);
                }
                synced = true;
                Trace(operation, "sync-completed");
                foreach (ChangeLogEntry entry in logs) entry.SourceDocumentTitle = Path.GetFileNameWithoutExtension(centralPath);
                ChangeLog.Append(logs);
                messages.Add("중앙모델 동기화를 완료했습니다. 변경 기록 " + logs.Count + "건." +
                    "\n기존 미동기화 작업도 함께 반영했습니다. 열린 문서와 소유권은 유지했습니다. 링크 표시는 필요 시 링크 관리에서 갱신해 주세요.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Trace(operation, synced ? "post-sync-error" : committed ? "sync-unconfirmed" : "apply-or-preflight-error");
                messages.Add((synced ? "중앙 동기화 이후 처리 오류: " : "작업을 완료하지 못했습니다: ") + ex.GetBaseException().Message);
                // Cancelled는 Revit 변경을 되돌린다. 복구할 열린 문서의 변경을 보존한다.
                return committed || synced ? Result.Succeeded : Result.Cancelled;
            }
            finally
            {
                Trace(operation, synced ? "finished-synced" : committed ? "finished-retained" : "finished-no-commit");
                System.Threading.Interlocked.Exchange(ref _running, 0);
                // 문서를 닫거나, 자동 저장·동기화 재시도·소유권 반납·링크 갱신을 하지 않는다.
                if (committed && !synced)
                    messages.Add("이번 변경은 열린 문서에 반영했지만 중앙 반영 완료 여부를 확인하지 못했습니다. 자동 재시도하지 마세요." +
                        "\n문서를 닫거나 변경을 버리지 않았습니다. 기존 작업과 함께 중앙 상태를 확인한 후 이 문서에서 직접 저장·동기화해 주세요:\n" + localPath);
                if (batch != null)
                {
                    batch.Synced = synced;
                    batch.RetainChanges = committed || synced;
                    batch.HasProblems = !synced || messages.Count != 1;
                    batch.Messages.AddRange(messages);
                    if (messages.Count == 0) batch.Messages.Add("미처리 — 변경 없음 또는 취소");
                }
                else if (messages.Count > 0) TaskDialog.Show(title + " — 링크 편집 결과", string.Join("\n\n", messages));
            }
        }
    }
}
