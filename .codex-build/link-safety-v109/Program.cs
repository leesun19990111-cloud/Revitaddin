using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WallSplitter;
class Program {
 static int checks;
 static void Check(bool ok,string text){if(!ok)throw new Exception(text+" / "+string.Join(",",S.Events));Console.WriteLine("PASS "+text);checks++;}
 static LinkedEditPlan Plan(Result result=Result.Succeeded)=>new(){Summary="이름 1개",Apply=(d,log)=>{S.Hit("edit");S.Changed=true;log.Add(new());return result;}};
 static Result Run(Func<Document,LinkedEditPlan?>? prepare=null)=>LinkedModelEditService.Run(new(),S.Link,"테스트",prepare??(_=>Plan()));
 static void Main(){
  S.Reset();Check(S.Host.GetWorksharingCentralModelPath() is not FilePath,"파일 경로를 기본 ModelPath로 반환하는 실제 API 래퍼 조건");
  Check(!LinkedModelEditService.IsFileBasedPath(null)&&!LinkedModelEditService.IsFileBasedPath(new ModelPath()),"없거나 빈 중앙 경로 거부");
  Check(LinkedModelEditService.IsFileBasedPath(new ModelPath{Value=@"\\server\share\central.rvt"}),"UNC 파일 경로는 Revit Server와 구분");
  S.Reset();Run();Check(S.Events.Count==0&&S.Messages.Any(m=>m.Contains("같은 Revit에 먼저")),"열리지 않은 대상은 자동 재열기 없이 안내");
  S.Reset();var report=new LinkedEditReport();var result=LinkedModelEditService.Run(new(),S.Link,"통합",_=>Plan(),report);
  Check(result==Result.Cancelled&&!report.Synced&&!report.RetainChanges&&report.HasProblems&&S.Events.Count==0,"일괄 경로도 원본 부재 시 모든 쓰기 없음");
  foreach(string kind in new[]{"central","version","cloud","nested","unloaded","readonly","modifiable","locallyunloaded","invalidlink"}){
   S.Reset();Open();
   switch(kind){case "central":S.NotCentral=true;break;case "version":S.OldVersion=true;break;case "cloud":S.Cloud=true;break;case "nested":S.Link.IsNestedLink=true;break;case "unloaded":S.Loaded=false;break;case "readonly":S.Host.IsReadOnly=true;break;case "modifiable":S.Host.IsModifiable=true;break;case "locallyunloaded":S.Link.LocallyUnloaded=true;break;case "invalidlink":S.Link.IsValidObject=false;break;}
   Run();Check(S.Events.Count==0,"잘못된 조건은 편집 전 차단: "+kind);
  }
  S.Reset();Open();bool rejected=false;try{LinkedModelEditService.RequireOpenTarget(new(),S.Link,S.HostCentral);}catch{rejected=true;}
  Check(rejected&&S.Events.Count==0,"조회 때와 다른 중앙 경로는 사전 차단");
  S.Reset();Open();LinkedModelEditService.RequireOpenTarget(new(),S.Link,S.Central);
  Check(S.Events.Count==0&&S.Confirmations.Count==0,"사전 검사는 읽기 전용이며 저장/확인/수정 없음");
  S.Reset();Open();Run(_=>new(){Apply=(d,l)=>Result.Succeeded});
  Check(!S.Synced&&S.Events.Contains("rollback"),"변경 0개는 동기화 안 함");
  S.Reset();Open();Run(d=>{
   var nested=new LinkedEditReport();var nestedResult=LinkedModelEditService.Run(new(),S.Link,"재진입",_=>Plan(),nested);
   Check(nestedResult==Result.Cancelled&&nested.HasProblems&&!nested.RetainChanges&&S.Events.Count==0,"중첩 실행은 쓰기 없이 차단");
   return Plan();
  });
  Check(S.Synced,"중첩 호출 거부가 바깥 실행을 방해하지 않음");
  S.Reset();Open();Run();Check(S.Synced,"완료 후 재진입 가드 해제");
  S.Reset();Open();S.Fail="edit";Run();S.Fail="";Run();Check(S.Synced,"오류 후 재진입 가드 해제");
  S.Reset();Open();Run(d=>{d.IsReadOnly=true;return Plan();});
  Check(S.Events.Count==0,"계획 준비 뒤 읽기 전용 변경도 적용 직전 재검사");
  S.Reset();Open();S.Fail="group-start";Run();Check(!S.Synced&&S.Logs==0,"그룹 시작 오류는 동기화/거짓 기록 없음");
  S.Reset();Open();Run();Check(S.Events.IndexOf("group-dispose")<S.Events.IndexOf("sync"),"그룹 Dispose 완료 후에만 중앙 동기화");
  S.Reset();S.Links.Clear();Check(LinkedModelEditService.Choose(new(),"테스트",out var link)&&link==null,"링크 없는 프로젝트는 기존 기능");
  S.Reset();S.Answers.Enqueue(TaskDialogResult.CommandLink1);Check(LinkedModelEditService.Choose(new(),"테스트",out link)&&link==null,"현재 프로젝트 분기");
  OpenDocumentTests();
  Console.WriteLine("ALL "+checks+" LINK SAFETY TESTS PASSED");
 }
 static Document Open(bool central=false){
  var path=central?S.Central:Path.GetFullPath("fixtures/existing-local.rvt");
  if(!central)File.WriteAllText(path,"이미 저장된 사용자 로컬");
  var d=new Document{PathName=path,Title="이미 열린 원본"};S.OpenDocs.Add(d);return d;
 }
 static void OpenDocumentTests(){
  S.Reset();var open=Open();Run(d=>{Check(ReferenceEquals(d,open),"편집 계획은 열린 원본에서 준비");return Plan();});
  Check(S.Synced&&ReferenceEquals(S.SyncDocument,open)&&open.IsValidObject,"열린 로컬을 정확히 동기화하고 유지");
  Check(!S.Events.Any(e=>new[]{"copy","open","close","save","unload-local","unload-global","restore-local","restore-global","relinquish"}.Contains(e)),"재사용 문서에는 생성·닫기·자동 반납·링크 변경 없음");
  Check(S.SyncOptions!.SaveLocalBefore&&S.SyncOptions.SaveLocalAfter&&!S.SyncOptions.RelinquishAll,"로컬 저장 및 기존 작업 소유권 유지 옵션");
  Check(S.Confirmations.Count==1&&S.Confirmations[0].Contains("미저장·미동기화")&&S.Confirmations[0].Contains(open.PathName),"열린 문서의 기존 작업 동기화 필수 확인");
  Check(S.Events.IndexOf("sync")<S.Events.IndexOf("log"),"재사용도 동기화 후에만 기록");
  S.Reset();open=Open(true);Run();
  Check(S.Synced&&!S.Events.Contains("save")&&!S.SyncOptions!.SaveLocalBefore&&!S.SyncOptions.SaveLocalAfter,"열린 중앙 원본은 Save 없이 중앙 동기화만 실행");
  Check(S.Confirmations.Single().Contains("중앙 원본을 직접"),"중앙 직접 열기 안내");
  S.Reset();open=Open();S.Multiple=true;Run();Check(S.Synced&&!S.Events.Contains("unload-local"),"여러 호스트에 로드돼도 열린 원본 재사용은 해제 없이 진행");
  S.Reset();open=Open();S.Host.PathName=S.HostCentral;Run();Check(S.Synced,"호스트도 중앙 직접 열기인 경우 링크 해제 없는 재사용 허용");
  S.Reset();open=Open();S.Answers.Enqueue(TaskDialogResult.No);Run();
  Check(S.Events.Count==0&&open.IsValidObject&&S.Loaded,"확인 취소는 기존 문서 저장·수정·동기화·해제 없음");
  S.Reset();open=Open();Run(_=>null);Check(S.Events.Count==0&&open.IsValidObject,"계획 취소에도 기존 문서 미변경");
  S.Reset();open=Open();Run(_=>Plan(Result.Failed));
  Check(S.Events.Contains("rollback")&&!S.Changed&&open.IsValidObject&&!S.Events.Contains("relinquish")&&!S.Synced,"일부 실패는 이번 그룹만 롤백하고 기존 소유권 유지");
  foreach(var step in new[]{"edit","group-commit","sync"}){
   S.Reset();open=Open();S.Fail=step;Run();
   Check(open.IsValidObject&&!S.Events.Contains("close")&&!S.Events.Contains("save")&&!S.Events.Contains("relinquish")&&S.Logs==0,"실패에도 열린 문서와 기존 작업 보존: "+step);
   if(step=="sync")Check(S.Changed&&S.Messages.Any(m=>m.Contains("이 문서에서 직접")&&m.Contains(open.PathName)&&m.Contains("자동 재시도")),"중앙 실패는 변경 보존·열린 문서 복구 경로 안내");
  }
  foreach(var state in new[]{"readonly","modifiable","detached","notshared","invalidcentral"}){
   S.Reset();open=Open(state=="notshared");
   switch(state){case "readonly":open.IsReadOnly=true;break;case "modifiable":open.IsModifiable=true;break;case "detached":open.IsDetached=true;break;case "notshared":open.IsWorkshared=false;break;case "invalidcentral":open.PathName=S.Central;S.LocalCentral=S.HostCentral;break;}
   Run();Check(S.Events.Count==0&&open.IsValidObject,"부적절한 열린 문서는 새 로컬로 우회하지 않고 차단: "+state);
  }
  S.Reset();Open();Open(true);Run();Check(S.Events.Count==0,"중복 편집 원본은 임의 선택하지 않음");
  S.Reset();Open();var report=new LinkedEditReport();LinkedModelEditService.Run(new(),S.Link,"통합",_=>Plan(),report);
  Check(report.Synced&&!report.HasProblems&&S.Confirmations.Count==1,"통합 일괄 경로에서도 기존 작업 확인을 생략하지 않음");
  S.Reset();Open();S.Answers.Enqueue(TaskDialogResult.No);report=new();LinkedModelEditService.Run(new(),S.Link,"통합",_=>Plan(),report);
  Check(!report.Synced&&report.HasProblems&&S.Events.Count==0,"일괄 재사용 확인 취소를 상위 처리에 전파");
  S.Reset();Open();S.Fail="sync";report=new();var result=LinkedModelEditService.Run(new(),S.Link,"통합",_=>Plan(),report);
  Check(!report.Synced&&report.HasProblems&&report.RetainChanges&&result==Result.Succeeded,"실패 상태와 변경 보존을 분리해 외부 명령의 자동 롤백 방지");
 }
}
