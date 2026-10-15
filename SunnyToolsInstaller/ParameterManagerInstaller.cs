using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

internal static class ParameterManagerInstaller
{
    internal static int Run()
    {
        Console.WriteLine("=== Sunny 매개변수 관리자 0.1.6 · Revit 2026 연결 모듈 설치 ===");
        Console.WriteLine("기존 Sunny Tools 애드인은 변경하지 않습니다.");
        try
        {
            if (Process.GetProcessesByName("Revit").Length > 0 || Process.GetProcessesByName("SunnyParameterManager").Length > 0)
                throw new InvalidOperationException("Revit과 매개변수 관리자를 모두 저장·종료한 뒤 설치하세요. 실행 중인 프로그램은 강제 종료하지 않습니다.");
            var payload = Path.Combine(AppContext.BaseDirectory, "ParameterManager");
            var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(payload, "files.sha256.json"))) ?? throw new InvalidDataException("배포 검증 목록이 없습니다.");
            foreach (var required in new[] { "SunnyParameterManager.exe", "SunnyParameterManager.dll", "SunnyParameterManager.runtimeconfig.json", "Bridge/SunnyParameterManager.Bridge.dll", "Bridge/SunnyParameterManager.Core.dll" })
                if (!hashes.ContainsKey(required)) throw new InvalidDataException("필수 파일이 없습니다: " + required);
            foreach (var pair in hashes)
            {
                var file = Path.GetFullPath(Path.Combine(payload, pair.Key));
                if (!file.StartsWith(Path.GetFullPath(payload) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("배포 경로 오류입니다.");
                using var stream = File.OpenRead(file);
                if (Convert.ToHexString(SHA256.HashData(stream)) != pair.Value) throw new InvalidDataException("배포 파일 검증 실패: " + pair.Key);
                if (Path.GetFileName(file).StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Autodesk API 파일은 배포하지 않습니다.");
            }
            var desktopRuntime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(desktopRuntime) || !Directory.GetDirectories(desktopRuntime).Any(p => Path.GetFileName(p).StartsWith("8.")))
                throw new InvalidOperationException(".NET 8 데스크톱 런타임(x64)이 필요합니다. Microsoft 공식 런타임을 설치한 뒤 다시 실행하세요.");
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var target = Path.Combine(local, "SunnyTools", "ParameterManager", "App-0.1.6-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(target);
            foreach (var pair in hashes)
            {
                var destination = Path.Combine(target, pair.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(payload, pair.Key), destination, false);
                using var stream = File.OpenRead(destination);
                if (Convert.ToHexString(SHA256.HashData(stream)) != pair.Value) throw new IOException("설치 파일 검증 실패: " + pair.Key);
            }
            var addins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", "2026");
            Directory.CreateDirectory(addins);
            var manifest = Path.Combine(addins, "SunnyParameterManager.addin");
            var xml = new XDocument(new XElement("RevitAddIns", new XElement("AddIn", new XAttribute("Type", "Application"),
                new XElement("Name", "SunnyParameterManager"), new XElement("Assembly", Path.Combine(target, "Bridge", "SunnyParameterManager.Bridge.dll")),
                new XElement("AddInId", "09CCEBFA-3B16-45F2-8E64-4BFAE9D5315A"), new XElement("FullClassName", "SunnyParameterManager.Bridge.WorkerApplication"),
                new XElement("VendorId", "Sunny"), new XElement("VendorDescription", "Sunny 매개변수 관리자 전용 연결"))));
            var staged = Path.Combine(target, "SunnyParameterManager.addin"); xml.Save(staged);
            if (File.Exists(manifest)) File.Copy(manifest, Path.Combine(target, "PreviousManifest.backup"), false);
            File.Copy(staged, manifest, true);
            var exe = Path.Combine(target, "SunnyParameterManager.exe");
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("바로가기 생성기를 찾을 수 없습니다.");
                dynamic shell = Activator.CreateInstance(shellType)!;
                var shortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Sunny 매개변수 관리자.lnk");
                if (File.Exists(shortcutPath)) File.Copy(shortcutPath, Path.Combine(target, "PreviousShortcut.lnk"), false);
                dynamic shortcut = shell.CreateShortcut(shortcutPath); shortcut.TargetPath = exe; shortcut.WorkingDirectory = target; shortcut.Description = "여러 RVT 매개변수 조회·편집"; shortcut.Save();
            }
            catch (Exception ex) { Console.WriteLine("바로가기는 만들지 못했습니다: " + ex.Message); }
            Console.WriteLine("설치 완료: " + exe);
            Console.WriteLine("바탕화면의 'Sunny 매개변수 관리자'를 실행하세요.");
            Console.WriteLine("대상 RVT를 미리 열 필요는 없습니다. 프로그램의 'Revit 연결'이 별도 작업용 Revit을 시작합니다.");
            Console.WriteLine("첫 버전입니다. 운영 중앙이 아닌 별도 테스트 모델에서 조회→소수 값 변경→저장/동기화를 먼저 검증하세요.");
            Pause(); return 0;
        }
        catch (Exception ex) { Console.WriteLine("설치 중단: " + ex.Message); Pause(); return 1; }
    }
    private static void Pause() { if (!Console.IsInputRedirected) { Console.WriteLine("아무 키나 누르면 종료합니다."); Console.ReadKey(true); } }
}
