// 배포 원본 → Payload → ZIP의 파일 집합과 SHA-256을 검사한다.
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto'), zlib = require('node:zlib'), assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const sha = b => crypto.createHash('sha256').update(b).digest('hex').toUpperCase();
const read = p => fs.readFileSync(p); // 없는 파일은 즉시 실패한다.
function walk(dir, prefix = '') {
  return fs.readdirSync(dir, {withFileTypes:true}).flatMap(e =>
    e.isDirectory() ? walk(path.join(dir,e.name), prefix+e.name+'/') : [prefix+e.name]).sort();
}
const published = path.join(root,'SunnyToolsInstaller_out_v108');
for (let year=2023; year<=2027; year++) {
  const src = path.join(root,'WallSplitter/bin/iso108'+year), dst = path.join(published,'Payload/'+year);
  const names = fs.readdirSync(src).filter(n => n.endsWith('.dll')).sort();
  assert.equal(names.length,year<2025?11:2);
  assert.deepEqual(fs.readdirSync(dst).sort(),names);
  assert(names.includes('Clipper2Lib.dll'));
  assert(!names.some(n=>/^RevitAPI/i.test(n)));
  for (const name of names) assert.equal(sha(read(path.join(src,name))),sha(read(path.join(dst,name))),year+'/'+name);
  const dll = read(path.join(dst,'WallSplitter.dll')).toString('latin1');
  for (const name of ['ApplyLevelSectionBox','PendingLevelSectionBox','ResolveLevelRange','BuildLevelRangePicker','LevelBottomName','ModelPlanExtent','TryParseFloorNumber'])
    assert(!dll.includes(name),'삭제된 코드 잔존: '+year+'/'+name);
  for (const name of ['WarningPickDeleteService','WarningPickDeletePreview','WarningPickDeleteRequest','ExecuteDelete','DeleteCheckedButton_Click','DeleteAllButton_Click','SetForcedModalHandling'])
    assert(dll.includes(name),'호환/다른 기능 누락: '+year+'/'+name);
  if (year>=2025) {
    const deps = JSON.parse(read(path.join(src,'WallSplitter.deps.json')));
    for (const target of Object.values(deps.targets))
      for (const lib of Object.values(target))
        for (const asset of Object.keys(lib.runtime||{}))
          assert(names.includes(path.basename(asset)),'런타임 DLL 누락: '+asset);
  }
  for (const name of ['RefreshFamilyTypes','WithFamilyTypes','ToggleFamily','_expandedFamilies','_displayElements','FamilyHierarchyHint','HierarchyWidth','ToggleFamilyTypes','UpdateFamilyTypeChecks','MaterialInstanceAssignment','InstanceMaterials','MaterialInstanceAssign','BuildAssignmentResults'])
    assert(dll.includes(name),'네이머 계층 누락: '+year+'/'+name);
  console.log('PASS Revit '+year+': '+names.length+' DLL, 네이머 계층 및 기존 안전장치 심볼 확인');
}
const zip = read(path.join(root,'SunnyTools_Installer_v.1.0.8.zip'));
let eocd = -1;
for (let i=zip.length-22;i>=Math.max(0,zip.length-65557);i--) if (zip.readUInt32LE(i)===0x06054b50){eocd=i;break;}
assert(eocd>=0);
const count=zip.readUInt16LE(eocd+10);let at=zip.readUInt32LE(eocd+16);const entries=new Map();
for(let i=0;i<count;i++){
  assert.equal(zip.readUInt32LE(at),0x02014b50);
  const method=zip.readUInt16LE(at+10), len=zip.readUInt32LE(at+20), nameLen=zip.readUInt16LE(at+28), extra=zip.readUInt16LE(at+30), comment=zip.readUInt16LE(at+32), offset=zip.readUInt32LE(at+42);
  const name=zip.subarray(at+46,at+46+nameLen).toString('utf8').replace(/\\/g,'/');
  if(!name.endsWith('/')){
    assert(!entries.has(name));
    assert.equal(zip.readUInt32LE(offset),0x04034b50);
    const start=offset+30+zip.readUInt16LE(offset+26)+zip.readUInt16LE(offset+28);
    const bytes=zip.subarray(start,start+len);
    assert(method===0||method===8);
    entries.set(name,method===0?bytes:zlib.inflateRawSync(bytes));
  }
  at+=46+nameLen+extra+comment;
}
const names=walk(published);
assert.equal(names.length,30);
assert.deepEqual([...entries.keys()].sort(),names);
for(const name of names) assert.equal(sha(entries.get(name)),sha(read(path.join(published,name))),name);
console.log('PASS ZIP: 30 files, exact names and SHA-256');
console.log('ZIP '+zip.length+' bytes SHA256 '+sha(zip));
console.log('EXE SHA256 '+sha(read(path.join(published,'SunnyTools_Install.exe'))));

const handler = read(path.join(root,'WallSplitter/WarningPickExternalEventHandler.cs')).toString('utf8');
assert(handler.includes('DefaultButton = TaskDialogResult.No'));
assert(handler.includes('dialog.Show() == TaskDialogResult.Yes'));
assert(handler.includes('!request.Document.IsValidObject'));
const ui = read(path.join(root,'WallSplitter/WarningPickWindow.xaml')).toString('utf8');
assert.equal((ui.match(/Style="{StaticResource DangerButtonStyle}"/g)||[]).length,2);
console.log('PASS: 기본 아니요/명시적 예/문서 검증/빨간 버튼 2개');
const materialUi = read(path.join(root,'WallSplitter/MaterialAssignWindow.xaml.cs')).toString('utf8');
assert(materialUi.includes('MessageBoxResult.No) != MessageBoxResult.Yes'));
assert(materialUi.includes('candidate.InstanceMaterials.Keys.ToList()'));
const replay=read(path.join(root,'WallSplitter/ChangeReplayEngine.cs')).toString('utf8');
assert(/case ChangeKind.MaterialInstanceAssign:\s*summary.Skipped.Add/.test(replay));
console.log('PASS: 인스턴스 전체 범위 확인창 기본 아니요, 전체 ID 전달, 타 모델 자동 재생 제외');



for (const file of ['MaterialSlotFinder.cs','NamerCommand.cs']) {
 const code=read(path.join(root,'WallSplitter',file)).toString('utf8');
 assert(!/\.SetLayers\s*\(/.test(code),'재료 변경 경로의 레이어 재생성 재발: '+file);
 assert(code.includes('.SetMaterialId('),'재료 전용 변경 경로 없음: '+file);
}
console.log('PASS: 재료 지정/네이머 병합 모두 SetLayers 호출 없음, SetMaterialId 사용');
const linked=read(path.join(root,'WallSplitter/LinkedModelEditService.cs')).toString('utf8');
for(const symbol of ['RequireOpenTarget','FindOpenTarget','SynchronizeWithCentral','TransactionGroup','ShouldWaitForLockAvailability','Interlocked.CompareExchange'])
 assert(linked.includes(symbol),'링크 안전 경로 누락: '+symbol);
assert(linked.indexOf('local.SynchronizeWithCentral')<linked.indexOf('ChangeLog.Append(logs)'),'중앙 동기화 이전 기록 금지');
assert(linked.includes('DefaultButton = TaskDialogResult.No'));
assert(!linked.includes('SaveAs(')&&!linked.includes('File.Copy('),'중앙 직접 덮어쓰기 금지');
for(let year=2023;year<=2027;year++){
 const dll=read(path.join(published,'Payload',String(year),'WallSplitter.dll')).toString('latin1');
 for(const name of ['LinkedModelEditService','LinkedEditPlan','get_LinkedRenames','SynchronizeWithCentral','RequireOpenTarget'])
  assert(dll.includes(name),'링크 기능 누락: '+year+'/'+name);
}
console.log('PASS: 열린 원본 전용·중앙 동기화·로그 지연 및 전 연도 안전 경로 심볼');
const unified=read(path.join(root,'WallSplitter/UnifiedEditService.cs')).toString('utf8');
const unifiedUi=read(path.join(root,'WallSplitter/UnifiedEditWindow.cs')).toString('utf8');
assert(unified.includes('OrderBy(b => b.Source.IsHost ? 1 : 0)'));
assert(unified.includes('stopped = !outcome.Synced || outcome.HasProblems'));
assert(unified.includes('liveIds.SetEquals(row.OriginalMaterials.Keys)'));
assert(unified.includes('doc.GetElement(row.UniqueId)'));
assert(!unifiedUi.includes('UIApplication'));
assert(!linked.includes('if (!hostInfo.IsLocal)'));
assert(linked.includes('RequireOpenTarget(app, link, centralPath)')&&linked.includes('GetFinalPathNameByHandle'));
for(const file of ['NamerCommand.cs','MaterialAssignCommand.cs']){
 const code=read(path.join(root,'WallSplitter',file)).toString('utf8');
 assert(code.includes('UnifiedEditService.Execute('));
 assert(!code.includes('LinkedModelEditService.Choose('));
}
for(let year=2023;year<=2027;year++){
 const dll=read(path.join(published,'Payload',String(year),'WallSplitter.dll')).toString('latin1');
 for(const name of ['UnifiedEditService','UnifiedEditWindow','UnifiedSource','UnifiedRow','ValidateHostLocal'])
  assert(dll.includes(name),'통합 편집 기능 누락: '+year+'/'+name);
}
console.log('PASS: 통합 진입점·소속 모델 분리·인스턴스 범위 재검증·중간 실패 중단·로컬 오판 회귀 방지');
assert(!/\bis\s+(not\s+)?FilePath\b/.test(linked+unified));
assert(linked.includes('!path.Empty && !path.ServerPath && !path.CloudPath'));
for(const symbol of ['RefreshPreview','BeginDrag','ContinueDrag','SyncColumns','CommitInline']) assert(unifiedUi.includes(symbol));
for(let year=2023;year<=2027;year++){
 const dll=read(path.join(published,'Payload',String(year),'WallSplitter.dll')).toString('latin1');
 for(const name of ['IsFileBasedPath','BeginDrag','ContinueDrag','SyncColumns','CommitInline']) assert(dll.includes(name));
}
console.log('PASS: FilePath 형식 오판 금지·경로 속성 검사·실시간 미리보기·드래그·열 너비 복원');
for(let year=2023;year<=2027;year++){
 const dll=read(path.join(published,'Payload',String(year),'WallSplitter.dll')).toString('latin1');
 for(const name of ['FindOpenTarget','ValidateOpenTarget','OpenDocumentPath','RetainChanges']) assert(dll.includes(name),'열린 문서 재사용 누락: '+year+'/'+name);
}
assert(linked.includes('new RelinquishOptions(false)'));
assert(!linked.includes('local.Close(')&&!linked.includes('local.Save('));
assert(unified.includes('retainChanges |= outcome.RetainChanges'));
assert(unified.includes('Document? open = LinkedModelEditService.FindOpenTarget(app, source.Path)'));
console.log('PASS: 열린 원본 조회·재사용·소유권 유지·자동 닫기 제외·복구 변경 보존');
for(let year=2023;year<=2027;year++){
 const dll=read(path.join(published,'Payload',String(year),'WallSplitter.dll')).toString('latin1');
 for(const name of ['ProjectInformationService','ProjectInformationPanel','ScheduleEditorCommand','ScheduleEditorWindow','ScheduleEditorLogic','ValidateGuards']) assert(dll.includes(name),'v100 기능 누락: '+year+'/'+name);
}
assert(unifiedUi.includes('재료 검색')&&!unifiedUi.includes('IsEditable = true'));
const schedule=read(path.join(root,'WallSplitter/ScheduleEditorCommand.cs')).toString('utf8');
assert(schedule.includes('LinkedModelEditService.Run')&&schedule.includes('retained |= outcome.RetainChanges'));
assert(replay.includes('case ChangeKind.ProjectInformation:')&&replay.includes('case ChangeKind.ScheduleParameter:'));
console.log('PASS: v100 프로젝트 정보·재료 검색·일람표 기능 및 링크 안전 경로');
const selection=read(path.join(root,'WallSplitter/ScheduleEditorWindow.cs')).toString('utf8');
for(const symbol of ['BeginDrag','ContinueDrag','ReadRange','FormatRange','RefreshSelection','UpdateDrag','_dragTimer.Stop()','_syncingRange']) assert(selection.includes(symbol));
assert(!selection.includes('UIApplication'));
for(let year=2023;year<=2027;year++) {
 const dll=read(path.join(published,'Payload',String(year),'WallSplitter.dll')).toString('latin1');
 for(const name of ['FormatRange','ReadRange','_syncingRange','_dragTimer']) assert(dll.includes(name),'일람표 선택 코드 누락: '+year+'/'+name);
}
console.log('PASS: v101 일람표 드래그·Shift·범위 입력 연동 및 타이머 정리');
const typed = read(path.join(root,'WallSplitter/ScheduleEditorParameters.cs')).toString('utf8');
const mapper = read(path.join(root,'WallSplitter/ScheduleEditorService.cs')).toString('utf8');
assert(typed.includes('UnitFormatUtils.Format(host.GetUnits()') && typed.includes('field.GetFormatOptions()'));
assert(mapper.includes('ScheduleEditorParameters.RawKey') && mapper.includes('ScheduleEditorLogic.MatchRows'));
assert(!mapper.includes('텍스트 필드가 필요합니다'));
for(let year=2023;year<=2027;year++) {
 const dll=read(path.join(published,'Payload',String(year),'WallSplitter.dll')).toString('latin1');
 for(const name of ['ScheduleEditorParameters','ConfigureEditors','StageReferences','ReferenceChoices','SelectedReference','_referenceEditors','_boolean']) assert(dll.includes(name),'형식별 일람표 편집 누락: '+year+'/'+name);
}
console.log('PASS: v102 숫자/단위/원시값 행 검증, 필드별 입력 UI, 모델별 참조 선택');
const sheets = read(path.join(root,'WallSplitter/ScheduleEditorSheets.cs')).toString('utf8');
assert(mapper.includes('ScheduleEditorSheets.Read(app, schedule)'));
assert(sheets.includes('OfClass(typeof(ViewSheet))') && sheets.includes('BuiltInParameter.SHEET_NUMBER'));
assert(sheets.includes('BuiltInParameter.RVT_LINK_FILE_NAME_WITHOUT_EXT') && sheets.includes('finally { tx.RollBack(); }'));
assert(sheets.includes('matches.GroupBy(m => m.Key)') && sheets.includes('guarded.Add(item.Key)'));
assert(sheets.includes('일람표 표시와 원본의 현재 값이 다릅니다'));
for(let year=2023;year<=2027;year++) {
 const dll=read(path.join(published,'Payload',String(year),'WallSplitter.dll')).toString('latin1');
 assert(dll.includes('ScheduleEditorSheets'),'도면목록표 전용 경로 누락: '+year);
}
console.log('PASS: v103 도면목록표 직접 수집·모델 분리·복제 롤백·원본 값 재검증');
assert(unifiedUi.includes('_sort.SelectionChanged') && unifiedUi.includes('Guard(Resort)'));
for(let year=2023;year<=2027;year++) {
 const dll=read(path.join(published,'Payload',String(year),'WallSplitter.dll')).toString('latin1');
 for(const name of ['Resort','BuildDisplay','_sort']) assert(dll.includes(name),'정렬 기능 누락: '+year+'/'+name);
}
console.log('PASS: v105 네이머/재료 지정 모델·이름 정렬 및 예외 보호');
for (const symbol of ['CreateNewLocal','OpenDocumentFile','UnloadLocally','RevertLocalUnloadStatus','RelinquishOwnership']) {
 assert(!linked.includes(symbol),'충돌 경로 재유입: '+symbol);
 for(let year=2023;year<=2027;year++)
  assert(!read(path.join(published,'Payload',String(year),'WallSplitter.dll')).toString('latin1').includes(symbol),'배포 DLL에 충돌 경로 잔존: '+year+'/'+symbol);
}
assert(!/link\.(Unload|Load)\s*\(/.test(linked));
assert(unified.indexOf('Preflight(app, batches.Select')<unified.indexOf('foreach (var batch in batches)'));
assert(schedule.indexOf('UnifiedEditService.Preflight(app, batches.Select')<schedule.indexOf('foreach (var batch in batches)'));
assert(linked.indexOf('Trace(operation, "sync-start")')<linked.indexOf('local.SynchronizeWithCentral'));
assert(linked.includes('batch.RetainChanges = committed || synced'));
console.log('PASS: v105 자동 해제/재열기/닫기 제거, 전체 모델 사전 검사, 단계 기록, 실패 변경 보존');
for(let year=2023;year<=2027;year++) {
 const dll=read(path.join(published,'Payload',String(year),'WallSplitter.dll')).toString('latin1');
 for(const name of ['ShowProjectInformation','UpdateCategoryLabels','SourceStatus']) assert(dll.includes(name),'프로젝트 정보 진입점 누락: '+year+'/'+name);
}
assert(unifiedUi.includes('프로젝트 정보 · 모든 모델') && unifiedUi.includes('원본 로컬 열기 필요'));
assert(unified.includes('source.Rows.AddRange(ProjectInformationService.Read(doc, source))'));
assert(unified.includes('ProjectInformationService.Apply(doc, rows.Where'));
console.log('PASS: v106 모든 모델 프로젝트 정보 바로가기·전용 열·열린 원본 준비 안내·공통 저장 경로');
const rowLogic=read(path.join(root,'WallSplitter/ScheduleEditorModel.cs')).toString('utf8');
assert(rowLogic.includes('indices.Sum(i => counts[i])') && rowLogic.includes('ApprovedMappings.Contains'));
assert(mapper.includes('current.Key != snapshot.Key') && mapper.includes('current.ProjectValue!.Raw != snapshot.ProjectValue!.Raw'));
assert(selection.includes('프로젝트 매개변수 연결 확인') && selection.includes('MessageBoxResult.No'));
for(let year=2023;year<=2027;year++) {
 const dll=read(path.join(published,'Payload',String(year),'WallSplitter.dll')).toString('latin1');
 for(const name of ['MatchRows','RequiredRows','ApprovedMappings','ParameterMappings','LinkValue','IsIfcEntity']) assert(dll.includes(name),'v107 일람표 기능 누락: '+year+'/'+name);
}
console.log('PASS: v107 중복 집합/개수 검증·프로젝트 매개변수 명시적 승인·열린 원본 대조·IFC 필드');
for(let year=2023;year<=2027;year++) {
 const dll=read(path.join(published,'Payload/'+year+'/WallSplitter.dll')).toString('latin1');
 for(const name of ['ScheduleEditorParameterIdentity','ParameterIdentity','Missing','ConnectionFailed','CreateDiagnosticsWindow','Diagnostics']) assert(dll.includes(name),'v108 빈 셀/진단 기능 누락: '+year+'/'+name);
}
console.log('PASS: v108 누락 매개변수 재검증·요소 연결 실패 구분·행 연결 진단');

