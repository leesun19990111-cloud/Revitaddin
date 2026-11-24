using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WallSplitter;
class Program {
 static int checks;
 static void Check(bool ok,string text){if(!ok)throw new Exception(text+" / "+string.Join(",",S.Events));Console.WriteLine("PASS "+text);checks++;}
 static LinkedEditPlan Plan(Result result=Result.Succeeded)=>new(){Summary="이름 1개",Apply=(d,log)=>{S.Hit("edit");S.Changed=true;log.Add(new());return result;}};
 static Result Run(Func<Document,LinkedEditPlan?>? prepare=null)=>LinkedModelEditService.Run(new(),S.Link,"테스트",prepare??(_=>Plan()));
 static void Main(){
  S.Reset();Check(Run()==Result.Succeeded&&S.Synced&&S.Logs==1&&S.Loaded,"동기화·기록·링크 복원");
  Check(S.Events.IndexOf("save")<S.Events.IndexOf("sync")&&S.Events.IndexOf("sync")<S.Events.IndexOf("log"),"복구 로컬 저장 → 동기화 → 기록 순서");
  Check(S.Events.Contains("unload-local")&&!S.Events.Contains("unload-global"),"공유 호스트는 현재 사용자만 해제");
  S.Reset();S.Host.IsWorkshared=false;Run();Check(S.Events.Contains("restore-global")&&S.Loaded,"비공유 호스트 링크 복원");
  S.Reset();S.Answers.Enqueue(TaskDialogResult.No);Run();Check(S.Events.Count==0,"시작 확인 취소는 파일·링크 미변경");
  S.Reset();Run(_=>null);Check(!S.Synced&&S.Logs==0&&S.Loaded,"편집 취소는 중앙 미변경·링크 복원");
  S.Reset();S.Answers.Enqueue(TaskDialogResult.Yes);S.Answers.Enqueue(TaskDialogResult.No);Run();Check(!S.Events.Contains("edit")&&!S.Synced&&S.Loaded,"최종 동기화 확인 취소는 쓰기 없음");
  S.Reset();Run(_=>Plan(Result.Failed));Check(S.Events.Contains("rollback")&&!S.Changed&&!S.Synced&&S.Logs==0,"일부 실패는 파일 전체 롤백·기록 없음");
  S.Reset();Run(_=>new(){Apply=(d,l)=>Result.Succeeded});Check(!S.Synced&&S.Events.Contains("rollback"),"변경 0개는 동기화 안 함");
  foreach(string stage in new[]{"copy","unload-local","open","edit","group-commit","sync"}){
   S.Reset();S.Fail=stage;Run();Check(S.Logs==0&&S.Loaded,"오류 복원과 거짓 기록 방지: "+stage);
   if(stage=="sync")Check(S.Saved&&S.Messages.Any(m=>m.Contains("자동 재시도"))&&File.Exists(S.Local.PathName),"동기화 실패는 복구 로컬 보존·불확실 상태 안내");
  }
  S.Reset();S.Fail="save";Run();Check(S.Local.IsValidObject&&!S.Events.Contains("close")&&!S.Synced,"저장 실패 시 미저장 문서를 닫지 않음");
  S.Reset();S.Fail="restore-local";Run();Check(S.Synced&&S.Logs==1&&S.Messages.Any(m=>m.Contains("복원 실패")),"중앙 성공·링크 갱신 실패를 구분");
  foreach(string kind in new[]{"central","version","cloud","nested","multiple","hostcentral","unloaded","readonly"}){
   S.Reset();
   switch(kind){case "central":S.NotCentral=true;break;case "version":S.OldVersion=true;break;case "cloud":S.Cloud=true;break;case "nested":S.Link.IsNestedLink=true;break;case "multiple":S.Multiple=true;break;case "hostcentral":S.HostIsCentral=true;break;case "unloaded":S.Loaded=false;break;case "readonly":S.Host.IsReadOnly=true;break;}
   Run();Check(S.Events.Count==0,"지원하지 않는 상태는 해제 전 차단: "+kind);
  }
  S.Reset();S.OpenDocs.Add(new(){PathName=S.Central});Run();Check(!S.Events.Contains("copy"),"이미 열린 같은 중앙모델 차단");
  S.Reset();S.Local.IsReadOnly=true;Run();Check(!S.Events.Contains("edit")&&S.Loaded,"열린 로컬 읽기 전용 검사");
  S.Reset();S.LocalCentral=Path.GetFullPath("fixtures/wrong.rvt");Run();Check(!S.Events.Contains("edit")&&S.Loaded,"다른 중앙 연결이면 미변경");
  S.Reset();S.Links.Clear();Check(LinkedModelEditService.Choose(new(),"테스트",out var link)&&link==null,"링크 없는 프로젝트는 기존 기능");
  S.Reset();S.Answers.Enqueue(TaskDialogResult.CommandLink1);Check(LinkedModelEditService.Choose(new(),"테스트",out link)&&link==null,"현재 프로젝트 분기");
  S.Reset();S.Answers.Enqueue(TaskDialogResult.CommandLink2);Check(LinkedModelEditService.Choose(new(),"테스트",out link)&&link==S.Link,"선택한 링크 하나만 전달");
  S.Reset();S.Answers.Enqueue(TaskDialogResult.CommandLink2);NamerWindow.Instance=new();Check(!LinkedModelEditService.Choose(new(),"테스트",out link),"열린 NAMER 편집 보호");
  Console.WriteLine("ALL "+checks+" LINK SAFETY TESTS PASSED");
 }
}

