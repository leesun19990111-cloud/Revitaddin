// 배포 원본 → Payload → ZIP의 파일 집합과 SHA-256을 검사한다.
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto'), zlib = require('node:zlib'), assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const sha = b => crypto.createHash('sha256').update(b).digest('hex').toUpperCase();
const read = p => fs.readFileSync(p); // 없는 파일은 즉시 실패한다.
function walk(dir, prefix = '') {
  return fs.readdirSync(dir, {withFileTypes:true}).flatMap(e =>
    e.isDirectory() ? walk(path.join(dir,e.name), prefix+e.name+'/') : [prefix+e.name]).sort();
}
const published = path.join(root,'SunnyToolsInstaller_out_v95');
for (let year=2023; year<=2027; year++) {
  const src = path.join(root,'WallSplitter/bin/iso95'+year), dst = path.join(published,'Payload/'+year);
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
const zip = read(path.join(root,'SunnyTools_Installer_v.0.9.5.zip'));
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
