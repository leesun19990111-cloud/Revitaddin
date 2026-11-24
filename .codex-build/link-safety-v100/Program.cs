using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WallSplitter;
class Program {
 static int checks;
 static void Check(bool ok,string text){if(!ok)throw new Exception(text+" / "+string.Join(",",S.Events));Console.WriteLine("PASS "+text);checks++;}
 static LinkedEditPlan Plan(Result result=Result.Succeeded)=>new(){Summary="이름 1개",Apply=(d,log)=>{S.Hit("edit");S.Changed=true;log.Add(new());return result;}};
 static Result Run(Func<Document,LinkedEditPlan?>? prepare=null)=>LinkedModelEditService.Run(new(),S.Link,"테스트",prepare??(_=>Plan()));
 static void Main(){
  S.Reset();Check(S.Host.GetWorksharingCentralModelPath() is not FilePath && Run()==Result.Succeeded,"기본 ModelPath로 반환된 파일 중앙·링크도 동기화 성공");
  Check(!LinkedModelEditService.IsFileBasedPath(null)&&!LinkedModelEditService.IsFileBasedPath(new ModelPath()),"없거나 빈 중앙 경로 거부");
  Check(LinkedModelEditService.IsFileBasedPath(new ModelPath{Value=@"\\server\share\central.rvt"}),"UNC 파일 경로는 Revit Server와 구분");
  S.Reset();S.HostServer=true;Run();Check(S.Events.Count==0,"Revit Server 호스트는 변경 전 차단");
  S.Reset();S.HostCloud=true;Run();Check(S.Events.Count==0,"클라우드 호스트는 변경 전 차단");
  S.Reset();S.HostCentral="";Run();Check(S.Events.Count==0,"빈 호스트 중앙 연결은 변경 전 차단");
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
   switch(kind){case "central":S.NotCentral=true;break;case "version":S.OldVersion=true;break;case "cloud":S.Cloud=true;break;case "nested":S.Link.IsNestedLink=true;break;case "multiple":S.Multiple=true;break;case "hostcentral":S.Host.PathName=S.HostCentral;break;case "unloaded":S.Loaded=false;break;case "readonly":S.Host.IsReadOnly=true;break;}
   Run();Check(S.Events.Count==0,"지원하지 않는 상태는 해제 전 차단: "+kind);
  }
  S.Reset();S.OpenDocs.Add(new(){PathName=S.Central});Run();Check(!S.Events.Contains("copy")&&S.Synced,"이미 열린 같은 중앙모델 재사용");
  S.Reset();S.Local.IsReadOnly=true;Run();Check(!S.Events.Contains("edit")&&S.Loaded,"열린 로컬 읽기 전용 검사");
  S.Reset();S.LocalCentral=Path.GetFullPath("fixtures/wrong.rvt");Run();Check(!S.Events.Contains("edit")&&S.Loaded,"다른 중앙 연결이면 미변경");
  S.Reset();S.Links.Clear();Check(LinkedModelEditService.Choose(new(),"테스트",out var link)&&link==null,"링크 없는 프로젝트는 기존 기능");
  S.Reset();S.Answers.Enqueue(TaskDialogResult.CommandLink1);Check(LinkedModelEditService.Choose(new(),"테스트",out link)&&link==null,"현재 프로젝트 분기");
  S.Reset();S.Answers.Enqueue(TaskDialogResult.CommandLink2);Check(LinkedModelEditService.Choose(new(),"테스트",out link)&&link==S.Link,"선택한 링크 하나만 전달");
  S.Reset();S.Answers.Enqueue(TaskDialogResult.CommandLink2);NamerWindow.Instance=new();Check(!LinkedModelEditService.Choose(new(),"테스트",out link),"열린 NAMER 편집 보호");
  S.Reset();S.HostIsCentral=true;Check(Run()==Result.Succeeded,"새 로컬의 저장 전 헤더 IsLocal=false도 실제 경로가 다르면 허용");
  S.Reset();S.Host.IsDetached=true;Run();Check(!S.Events.Contains("copy"),"중앙에서 분리한 호스트는 차단");
  S.Reset();S.Host.PathName=S.HostCentral.ToUpperInvariant();Run();Check(!S.Events.Contains("copy"),"대소문자가 달라도 중앙 원본이면 차단");
  S.Reset();S.HostCentral=Path.GetFullPath("fixtures/missing-central.rvt");Run();Check(!S.Events.Contains("copy"),"중앙 경로 검증 불가 시 변경 전 차단");
  S.Reset();var report=new LinkedEditReport();LinkedModelEditService.Run(new(),S.Link,"통합",_=>Plan(),report);
  Check(report.Synced&&!report.HasProblems&&S.Messages.Count==0&&report.Messages.Count==1,"통합 적용은 모델별 팝업 없이 성공 결과 수집");
  S.Reset();S.Fail="sync";report=new();LinkedModelEditService.Run(new(),S.Link,"통합",_=>Plan(),report);
  Check(!report.Synced&&report.HasProblems&&report.Messages.Any(m=>m.Contains("자동 재시도")),"통합 동기화 실패의 불확실 상태 전파");
  S.Reset();S.Fail="restore-local";report=new();LinkedModelEditService.Run(new(),S.Link,"통합",_=>Plan(),report);
  Check(report.Synced&&report.HasProblems,"통합 중앙 성공과 링크 복원 실패를 구별");
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
