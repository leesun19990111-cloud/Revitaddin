// 별도 제품의 반복 가능한 빌드/검증. 실제 Revit 설치 폴더나 사용자 RVT는 변경하지 않는다.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const output = path.join(root, 'SunnyParameterManagerInstaller_out_v017');
const zip = path.join(root, 'SunnyParameterManager_Installer_v.0.1.7.zip');
const scratch = path.join(root, 'SunnyParameterManager', 'Build', 'v017');
const extras = ['먼저 읽어주세요.md', '룰셋 파일 안내.md', '연습용.sunnyrules.json', '빌드정보.json'];
const sha = data => crypto.createHash('sha256').update(data).digest('hex').toUpperCase();
const files = dir => fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => e.isDirectory() ? files(path.join(dir, e.name)) : [path.join(dir, e.name)]);
const productFiles = dir => files(dir).filter(f => /^SunnyParameterManager(?:\.[\w-]+)*\.(exe|dll|pdb|json)$/i.test(path.basename(f)));
const rel = (base, file) => path.relative(base, file).replaceAll('\\', '/');
function dotnet(args) { execFileSync('dotnet', [...args, '-p:UseSharedCompilation=false'], { cwd: root, stdio: 'inherit', windowsHide: true }); }
function verify() {
  const payload = path.join(output, 'ParameterManager');
  const hashes = JSON.parse(fs.readFileSync(path.join(payload, 'files.sha256.json'), 'utf8'));
  for (const [name, hash] of Object.entries(hashes)) {
    if (sha(fs.readFileSync(path.join(payload, name))) !== hash) throw Error('페이로드 해시 불일치: ' + name);
    if (/RevitAPI|\.rvt$|\.rfa$/i.test(name)) throw Error('배포 금지 파일: ' + name);
  }
  const listingBytes = execFileSync('tar', ['-tf', zip], { windowsHide: true });
  const utf8 = listingBytes.toString('utf8');
  const listing = utf8.includes('\uFFFD') ? new TextDecoder('euc-kr').decode(listingBytes) : utf8;
  const listed = listing.trim().split(/\r?\n/).filter(s => !s.endsWith('/'));
  const expected = ['SunnyTools_Install.exe', 'SunnyTools_Install.pdb', ...extras, 'ParameterManager/files.sha256.json', ...Object.keys(hashes).map(n => 'ParameterManager/' + n)];
  if (JSON.stringify([...listed].sort()) !== JSON.stringify([...expected].sort())) throw Error('ZIP 구성 불일치');
  for (const name of expected) {
    const data = execFileSync('tar', ['-xOf', zip, name], { maxBuffer: 128 * 1024 * 1024, windowsHide: true });
    if (sha(data) !== sha(fs.readFileSync(path.join(output, name)))) throw Error('ZIP 해시 불일치: ' + name);
  }
  if (fs.existsSync(path.join(output, 'Payload'))) throw Error('기존 Sunny Tools 페이로드 혼입');
  console.log(`검증 완료: ${Object.keys(hashes).length}개 프로그램 파일 / ZIP ${expected.length}개 파일 해시 일치`);
  console.log(zip);
}
if (process.argv.includes('--verify')) { verify(); process.exit(0); }
const packageOnly = process.argv.includes('--package-only');
if (!packageOnly && (fs.existsSync(output) || fs.existsSync(zip))) throw Error('기존 배포를 덮어쓰지 않습니다. 다음 버전 경로를 지정하거나 기존 배포를 검토하세요.');
if (!packageOnly) {
fs.mkdirSync(scratch, { recursive: true });
dotnet(['run', '--project', 'SunnyParameterManager/Tests/SunnyParameterManager.Tests.csproj', '-c', 'Release',
  '-p:IntermediateOutputPath=obj/Tests017/', '-p:OutputPath=bin/Tests017/']);
dotnet(['run', '--project', 'SunnyParameterManager/LifecycleTests/SunnyParameterManager.LifecycleTests.csproj', '-c', 'Release',
  '-p:IntermediateOutputPath=obj/Release017/', '-p:OutputPath=bin/Release017/']);
dotnet(['run', '--project', 'SunnyParameterManager/MaterialTests/SunnyParameterManager.MaterialTests.csproj', '-c', 'Release',
  '-p:IntermediateOutputPath=obj/Material017/', '-p:OutputPath=bin/Material017/']);
dotnet(['publish', 'SunnyParameterManager/Desktop/SunnyParameterManager.Desktop.csproj', '-c', 'Release',
  '-p:IntermediateOutputPath=obj/Publish017/', '-p:OutputPath=bin/Publish017/', '-o', path.join(scratch, 'Desktop')]);
dotnet(['build', 'SunnyParameterManager/Bridge/SunnyParameterManager.Bridge.csproj', '-c', 'Release',
  '-p:IntermediateOutputPath=obj/Bridge017/', '-p:OutputPath=bin/Bridge017/', '-o', path.join(scratch, 'Bridge')]);
dotnet(['publish', 'SunnyToolsInstaller/SunnyToolsInstaller.csproj', '-c', 'Release', '-p:ParameterManagerOnly=true',
  '-p:BaseIntermediateOutputPath=obj/ParameterManager017/', '-p:MSBuildProjectExtensionsPath=obj/ParameterManager017/',
  '-p:BaseOutputPath=bin/ParameterManager017/', '-o', output]);
}
const payload = path.join(output, 'ParameterManager');
fs.mkdirSync(path.join(payload, 'Bridge'), { recursive: true });
const hashes = {};
for (const [buildDir, destination] of [['Desktop', payload], ['Bridge', path.join(payload, 'Bridge')]]) {
  for (const file of productFiles(path.join(scratch, buildDir))) {
    const target = path.join(destination, path.basename(file));
    fs.copyFileSync(file, target);
    const sourceHash = sha(fs.readFileSync(file));
    if (sha(fs.readFileSync(target)) !== sourceHash) throw Error('빌드→페이로드 해시 불일치');
    hashes[rel(payload, target)] = sourceHash;
  }
}
fs.writeFileSync(path.join(payload, 'files.sha256.json'), JSON.stringify(hashes, null, 2));
fs.copyFileSync(path.join(root, 'docs/parameter-manager/사용안내.md'), path.join(output, '먼저 읽어주세요.md'));
fs.copyFileSync(path.join(root, 'docs/parameter-manager/룰셋 파일 안내.md'), path.join(output, '룰셋 파일 안내.md'));
fs.copyFileSync(path.join(root, 'SunnyParameterManager/Examples/연습용.sunnyrules.json'), path.join(output, '연습용.sunnyrules.json'));
const sourceChecks = {};
for (const file of files(path.join(root, 'SunnyParameterManager'))) {
  if (!/[/\\](bin|obj|Build)[/\\]/.test(file) && /\.(cs|csproj|xaml|cjs)$/.test(file)) sourceChecks[rel(root, file)] = sha(fs.readFileSync(file));
}
for (const name of ['Program.cs', 'ParameterManagerInstaller.cs', 'SunnyToolsInstaller.csproj']) sourceChecks['SunnyToolsInstaller/' + name] = sha(fs.readFileSync(path.join(root, 'SunnyToolsInstaller', name)));
fs.writeFileSync(path.join(output, '빌드정보.json'), JSON.stringify({ version: '0.1.7', builtUtc: new Date().toISOString(), tests: '231 룰셋·core·WPF + 41 lifecycle + 22 재료 리더 API 대역 (빌드 전 전체 실행); 선택한 실무 룰셋·조회 기록 추가 검사 별도', liveRevitTested: false, sourceChecks }, null, 2));
// 보안 제품이 폴더 열거 시 만드는 임시 미끼 파일을 읽거나 배포하지 않는다. 검증한 파일만 지정한다.
const archiveFiles = ['SunnyTools_Install.exe', 'SunnyTools_Install.pdb', ...extras, 'ParameterManager/files.sha256.json', ...Object.keys(hashes).map(n => 'ParameterManager/' + n)];
execFileSync('tar', ['-a', '-cf', zip, '-C', output, ...archiveFiles], { stdio: 'inherit', windowsHide: true });
verify();
