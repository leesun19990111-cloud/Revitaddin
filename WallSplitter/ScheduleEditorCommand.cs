using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    [Transaction(TransactionMode.Manual)]
    public sealed class ScheduleEditorCommand : IExternalCommand
    {
        private static string Fingerprint(ScheduleEditorSnapshot data) => ScheduleEditorLogic.Key(data.Rows.Select(r =>
            ScheduleEditorLogic.Key(new[] { r.Identity }.Concat(r.Cells.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)))));
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            bool retained = false;
            try
            {
                var app = commandData.Application; var host = app.ActiveUIDocument.Document;
                var schedules = new FilteredElementCollector(host).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Where(s => !s.IsTemplate).Cast<Element>().ToList();
                var picker = new ElementPickerWindow("수정할 일람표를 선택하세요. 일람표의 정렬·필터를 유지한 데이터 행을 읽습니다.", schedules) { Title = "Schedule Editor · 일람표 선택" };
                new WindowInteropHelper(picker) { Owner = app.MainWindowHandle };
                if (picker.ShowDialog() != true || picker.Result is not ViewSchedule schedule) return Result.Cancelled;
                var data = ScheduleEditorService.Read(app, schedule);
                var window = new ScheduleEditorWindow(data); new WindowInteropHelper(window) { Owner = app.MainWindowHandle };
                if (window.ShowDialog() != true) return Result.Cancelled;
                var batches = data.Pending.Values.GroupBy(r => r.Source.Key).OrderBy(g => g.First().Source.IsHost ? 1 : 0).ToList();
                UnifiedEditService.Preflight(app, batches.Select(g => g.First().Source));
                string summary = string.Join("\n", batches.Select(g => g.First().Source.Name + " · " + g.Count() + "개 매개변수\n" + g.First().Source.Path));
                if (!LinkedModelEditService.Confirm("Schedule Editor", "미리보기의 변경을 실제 모델에 적용할까요?",
                    summary + "\n\n묶인 행은 조회 때 확인한 요소 모두를 수정합니다. 새로 추가된 요소로 범위를 넓히지 않습니다." +
                    "\n유형 값은 선택 밖의 같은 유형에도 적용됩니다. 텍스트 빈 값은 기존 값을 지웁니다." +
                    "\n링크 중앙모델로 동기화합니다. 이미 열린 원본/로컬의 기존 변경도 함께 동기화되므로 별도로 확인합니다." +
                    "\n현재 모델은 마지막에 반영하며 자동 저장/동기화하지 않습니다. 다른 사용자의 로컬은 그 사용자가 최신 항목을 불러와야 갱신됩니다." +
                    "\n실패 시 나머지 모델을 중단합니다. 이미 중앙에 동기화된 모델은 자동으로 되돌릴 수 없습니다.")) return Result.Cancelled;
                var fresh = ScheduleEditorService.Read(app, schedule);
                if (Fingerprint(fresh) != Fingerprint(data)) throw new InvalidOperationException("조회 이후 일람표의 행/값/소속이 변경되었습니다. 다시 조회해 주세요.");
                var report = new List<string>(); bool stopped = false;
                foreach (var batch in batches)
                {
                    var rows = batch.ToList(); var source = rows[0].Source;
                    if (stopped) { report.Add(source.Name + " — 미처리 (앞선 모델 실패)"); continue; }
                    try
                    {
                        Result Apply(Document doc, List<ChangeLogEntry> logs)
                        {
                            ScheduleEditorService.ValidateGuards(doc, data, source.Key);
                            ProjectInformationService.Apply(doc, rows, logs, true); return Result.Succeeded;
                        }
                        if (source.IsHost)
                        {
                            using var group = new TransactionGroup(host, "일람표 에디터"); group.Start();
                            var logs = new List<ChangeLogEntry>(); Apply(host, logs);
                            if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("모델 반영 취소");
                            retained = true; ChangeLog.Append(logs); report.Add(source.Name + " — 반영 완료 (저장/동기화는 직접 실행)");
                        }
                        else
                        {
                            var link = host.GetElement(source.LinkUniqueId) as RevitLinkType ?? throw new InvalidOperationException("링크가 삭제되었습니다.");
                            string path = ModelPathUtils.ConvertModelPathToUserVisiblePath(link.GetExternalFileReference().GetAbsolutePath());
                            if (!LinkedModelEditService.SamePath(path, source.Path)) throw new InvalidOperationException("링크 경로가 변경되었습니다.");
                            var outcome = new LinkedEditReport();
                            LinkedModelEditService.Run(app, link, "Schedule Editor", _ => new LinkedEditPlan { Summary = source.Name + " · " + rows.Count + "개 매개변수", Apply = Apply }, outcome);
                            retained |= outcome.RetainChanges;
                            report.Add(source.Name + " — " + string.Join("\n", outcome.Messages)); stopped = !outcome.Synced || outcome.HasProblems;
                        }
                    }
                    catch (Exception ex) { stopped = true; report.Add(source.Name + " — 실패: " + ex.GetBaseException().Message); }
                }
                TaskDialog.Show("Schedule Editor · 결과", string.Join("\n\n", report));
                return stopped && !retained ? Result.Cancelled : Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Schedule Editor", "실행을 완료하지 못했습니다.\n" + ex.GetBaseException().Message);
                return retained ? Result.Succeeded : Result.Cancelled;
            }
        }
    }
}
