// 배포 원본 → Payload → ZIP의 파일 집합과 SHA-256을 검사한다.
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto'), zlib = require('node:zlib'), assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const sha = b => crypto.createHash('sha256').update(b).digest('hex').toUpperCase();
const read = p => fs.readFileSync(p); // 없는 파일은 즉시 실패한다.
function walk(dir, prefix = '') {
  return fs.readdirSync(dir, {withFileTypes:true}).flatMap(e =>
    e.isDirectory() ? walk(path.join(dir,e.name), prefix+e.name+'/') : [prefix+e.name]).sort();
}
const published = path.join(root,'SunnyToolsInstaller_out_v92');
for (let year=2023; year<=2027; year++) {
  const src = path.join(root,'WallSplitter/bin/iso92'+year), dst = path.join(published,'Payload/'+year);
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
  console.log('PASS Revit '+year+': '+names.length+' DLL, 삭제 버튼·안전 삭제 심볼 확인');
}
const zip = read(path.join(root,'SunnyTools_Installer_v.0.9.2.zip'));
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
