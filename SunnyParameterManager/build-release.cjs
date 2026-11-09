// 별도 제품의 반복 가능한 빌드/검증. 실제 Revit 설치 폴더나 사용자 RVT는 변경하지 않는다.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const output = path.join(root, 'SunnyParameterManagerInstaller_out_v051');
const zip = path.join(root, 'SunnyParameterManager_Installer_v.0.5.1.zip');
const scratch = path.join(root, 'SunnyParameterManager', 'Build', 'v051');
const extras = ['먼저 읽어주세요.md', '룰셋 파일 안내.md', '연습용.sunnyrules.json', '중앙모델_분리재생성.sunnyrules.json', '프로젝트매개변수_표준전송.sunnyrules.json', '빌드정보.json'];
const sha = data => crypto.createHash('sha256').update(data).digest('hex').toUpperCase();
const files = dir => fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => e.isDirectory() ? files(path.join(dir, e.name)) : [path.join(dir, e.name)]);
const productFiles = dir => files(dir).filter(f => /^SunnyParameterManager(?:\.[\w-]+)*\.(exe|dll|pdb|json)$/i.test(path.basename(f)));
const rel = (base, file) => path.relative(base, file).replaceAll('\\', '/');
function dotnet(args) { execFileSync('dotnet', [...args, '-p:UseSharedCompilation=false'], { cwd: root, stdio: 'inherit', windowsHide: true }); }
function archive(mode) { execFileSync('pwsh', ['-NoProfile', '-File', path.join(__dirname, 'package-release.ps1'), '-Mode', mode, '-OutputPath', output, '-ZipPath', zip], { cwd: root, stdio: 'inherit', windowsHide: true }); }
function verify() {
  const payload = path.join(output, 'ParameterManager');
  const hashes = JSON.parse(fs.readFileSync(path.join(payload, 'files.sha256.json'), 'utf8'));
  for (const [name, hash] of Object.entries(hashes)) {
    if (sha(fs.readFileSync(path.join(payload, name))) !== hash) throw Error('페이로드 해시 불일치: ' + name);
    if (/RevitAPI|\.rvt$|\.rfa$/i.test(name)) throw Error('배포 금지 파일: ' + name);
  }
  const expected = ['SunnyTools_Install.exe', 'SunnyTools_Install.pdb', ...extras, 'ParameterManager/files.sha256.json', ...Object.keys(hashes).map(n => 'ParameterManager/' + n)];
  archive('Verify');
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
  '-p:IntermediateOutputPath=obj/Tests051/', '-p:OutputPath=bin/Tests051/']);
dotnet(['run', '--project', 'SunnyParameterManager/LifecycleTests/SunnyParameterManager.LifecycleTests.csproj', '-c', 'Release',
  '-p:IntermediateOutputPath=obj/Release051/', '-p:OutputPath=bin/Release051/']);
dotnet(['run', '--project', 'SunnyParameterManager/MaterialTests/SunnyParameterManager.MaterialTests.csproj', '-c', 'Release',
  '-p:IntermediateOutputPath=obj/Material051/', '-p:OutputPath=bin/Material051/']);
dotnet(['run', '--project', 'SunnyParameterManager/ReaderTests/SunnyParameterManager.ReaderTests.csproj', '-c', 'Release',
  '-p:IntermediateOutputPath=obj/Reader051/', '-p:OutputPath=bin/Reader051/']);
dotnet(['run', '--project', 'SunnyParameterManager/LinkTests/SunnyParameterManager.LinkTests.csproj', '-c', 'Release',
  '-p:IntermediateOutputPath=obj/Links051/', '-p:OutputPath=bin/Links051/']);
dotnet(['run', '--project', 'SunnyParameterManager/WorkerTests/SunnyParameterManager.WorkerTests.csproj', '-c', 'Release',
  '-p:IntermediateOutputPath=obj/Worker051/', '-p:OutputPath=bin/Worker051/']);
dotnet(['run', '--project', 'SunnyParameterManager/DefinitionTests/SunnyParameterManager.DefinitionTests.csproj', '-c', 'Release',
  '-p:IntermediateOutputPath=obj/Definitions051/', '-p:OutputPath=bin/Definitions051/']);
dotnet(['publish', 'SunnyParameterManager/Desktop/SunnyParameterManager.Desktop.csproj', '-c', 'Release',
  '-p:IntermediateOutputPath=obj/Publish051/', '-p:OutputPath=bin/Publish051/', '-o', path.join(scratch, 'Desktop')]);
dotnet(['build', 'SunnyParameterManager/Bridge/SunnyParameterManager.Bridge.csproj', '-c', 'Release',
  '-p:IntermediateOutputPath=obj/Bridge051/', '-p:OutputPath=bin/Bridge051/', '-o', path.join(scratch, 'Bridge')]);
dotnet(['publish', 'SunnyToolsInstaller/SunnyToolsInstaller.csproj', '-c', 'Release', '-p:ParameterManagerOnly=true',
  '-p:BaseIntermediateOutputPath=obj/ParameterManager051/', '-p:MSBuildProjectExtensionsPath=obj/ParameterManager051/',
  '-p:BaseOutputPath=bin/ParameterManager051/', '-o', output]);
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
fs.copyFileSync(path.join(root, 'SunnyParameterManager/Examples/중앙모델_분리재생성.sunnyrules.json'), path.join(output, '중앙모델_분리재생성.sunnyrules.json'));
fs.copyFileSync(path.join(root, 'SunnyParameterManager/Examples/프로젝트매개변수_표준전송.sunnyrules.json'), path.join(output, '프로젝트매개변수_표준전송.sunnyrules.json'));
const sourceChecks = {};
for (const file of files(path.join(root, 'SunnyParameterManager'))) {
  if (!/[/\\](bin|obj|Build)[/\\]/.test(file) && /\.(cs|csproj|xaml|cjs|ps1)$/.test(file)) sourceChecks[rel(root, file)] = sha(fs.readFileSync(file));
}
for (const name of ['Program.cs', 'ParameterManagerInstaller.cs', 'SunnyToolsInstaller.csproj']) sourceChecks['SunnyToolsInstaller/' + name] = sha(fs.readFileSync(path.join(root, 'SunnyToolsInstaller', name)));
fs.writeFileSync(path.join(output, '빌드정보.json'), JSON.stringify({ version: '0.5.1', builtUtc: new Date().toISOString(), tests: 'Core/WPF·Lifecycle·Material·Reader·Links·Worker·Definition 전체 (빌드 전 실행)', liveRevitTested: false, liveDiagnostic: '0.5.0 현장 실행에서 화면 밖 프로젝트 매개변수 체크박스를 찾지 못한 오류를 수정했다. 전체 자동 제어와 운영 중앙 저장·동기화는 아직 현장 재검증이 필요하다.', sourceChecks }, null, 2));
// 보안 제품이 폴더 열거 시 만드는 임시 미끼 파일을 읽거나 배포하지 않는다. 검증한 파일만 지정한다.
const archiveFiles = ['SunnyTools_Install.exe', 'SunnyTools_Install.pdb', ...extras, 'ParameterManager/files.sha256.json', ...Object.keys(hashes).map(n => 'ParameterManager/' + n)];
archive('Create');
verify();
