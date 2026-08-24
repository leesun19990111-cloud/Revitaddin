# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Revit external command add-in (C#/.NET) named **WallSplitter** ("Sunny Tools" ribbon tab), supporting **Revit 2023–2027** from a single codebase (multi-targeted, one build per year). The detailed engineering history (features, live bugs, root causes, "don't revert this" warnings) is split by feature area into `docs/<area>/CLAUDE.md` files — this root file only holds absolute rules and the routing table below.

## 절대 규칙

- **작업 전 필독**: 아래 "작업 영역별 문서" 표에서 건드리려는 파일/기능을 찾아, 그 문서를 먼저 읽고 나서 작업할 것. 각 문서에는 그 기능의 라이브 버그 이력, 근본 원인, "재테스트 없이 되돌리지 말 것" 경고가 들어있다 — 이 루트 파일에는 없다.
- **배포는 반드시 설치 프로그램으로만** (`docs/installer/CLAUDE.md`): `SunnyToolsInstaller`를 퍼블리시해서 배포한다. Debug/Release DLL을 사용자의 실제 Revit Addins 폴더(`%APPDATA%\Autodesk\Revit\Addins\<year>\`)에 직접 복사하지 않는다.
- **UI 문자열/코드 주석은 한국어**로 유지 — 기존 파일과 일관성.
- **새 설치 프로그램 산출물 → 이전 버전 아카이브**: `SunnyToolsInstaller_out_vN`을 새로 만들면, 이전 버전 폴더를 `Old_Versions/`로 옮기고 최신 것만 루트에 남긴다.
- **기능/동작 변경 시 `README.md`도 함께 갱신** — README는 사용자/GitHub용 짧은 요약, `docs/`는 상세 엔지니어링 로그. 상세 이력을 README에 옮기지 말 것.
- WPF 코드비하인드에서 `Autodesk.Revit.DB`와 `System.Windows`를 같이 쓰면 `Visibility`/`Grid`/`Control`/`Color`/`Binding`/`Line`/`Point` 등의 이름이 겹친다 — 완전한 이름 또는 별칭(`using X = ...`)으로 항상 구분할 것 (사례: `docs/design-system/CLAUDE.md`).
- `SplitWallCommand`의 3단계 트랜잭션 구조(Tx1 → 프로파일 스코프 → Tx2)를 하나로 합치지 말 것 — `SketchEditScope`는 열린 트랜잭션 중엔 `Start()`할 수 없다 (자세한 이유: `docs/wall-floor-split/CLAUDE.md`).

## 작업 영역별 문서 (작업 전 필독)

| 작업/기능 | 주요 파일 | 문서 |
|---|---|---|
| 벽체 분리 / 바닥 분리, 유형 이름 규칙 | `Class1.cs`, `SplitFloorCommand.cs`, `NamingSettings.cs`, `SettingsWindow.*` | `docs/wall-floor-split/CLAUDE.md` |
| 리본/앱 시작 (탭·패널·아이콘·매니페스트) | `App.cs`, `WallSplitter.addin`, `Resources/icon*.png` | `docs/app-shell/CLAUDE.md` |
| NAMER (이름 일괄 변경) | `NamerWindow.*`, `NamerCommand.cs` | `docs/namer/CLAUDE.md` |
| 재료 지정/삭제/클래스·설명 변경 | `MaterialAssignWindow.*`, `MaterialAssignCommand.cs`, `MaterialSlotFinder.cs` | `docs/material-assign/CLAUDE.md` |
| 모델간 변경 반영 | `ModelSyncWindow.*`, `ModelSyncCommand.cs`, `ChangeLog.cs`, `ChangeReplayEngine.cs` | `docs/model-sync/CLAUDE.md` |
| 패턴 스튜디오 (Revit/PAT 채우기 패턴 편집) | `Pattern*.cs`, `PatternStudioWindow.*`, `PatFileService.cs` | `docs/pattern-studio/CLAUDE.md` |
| 커스텀 버튼 (구 "빠른 토글" — 뷰템플릿/필터/작업세트, 뷰 저장·되돌리기) | `QuickToggle*.cs`, `QuickToggleToolbar.*`, `QuickToggleSettingsWindow.*` | `docs/quick-toggle/CLAUDE.md` |
| 화면 디자인 (Industry 테마, 아이콘) | `Resources/Theme.xaml`, `Theme.cs` | `docs/design-system/CLAUDE.md` |
| 멀티 버전 빌드 (2023–2027 Configuration/TFM 매핑) | `WallSplitter.csproj` | `docs/build-system/CLAUDE.md` |
| 설치 프로그램 (배포) | `SunnyToolsInstaller/` | `docs/installer/CLAUDE.md` |

## 프로젝트 구조

- `WallSplitter.slnx` — 솔루션 파일(slnx 형식). `WallSplitter/WallSplitter.csproj`(애드인 본체)와 `SunnyToolsInstaller/SunnyToolsInstaller.csproj`(설치 프로그램)를 참조한다.
- `WallSplitter/WallSplitter.addin` — **dev-mode** 매니페스트. `<Assembly>` 경로가 `WallSplitter/bin/Debug2026/net8.0-windows/WallSplitter.dll`(이 PC의 주력 버전)을 직접 가리킨다 — 다른 사용자/버전을 위한 배포 수단이 아니라 이 PC의 개발용 편의 파일이다.
- `Old_Versions/` — 과거 설치 프로그램 산출물 보관용 (최신 산출물은 루트에 별도 유지).
- `docs/<area>/CLAUDE.md` — 기능별 상세 문서 (위 라우팅 표 참고).

## Commands

- 애드인 한 연도만 빌드: `dotnet build WallSplitter/WallSplitter.csproj -c Release2026` (또는 `Debug2026`, 2023~2027 아무 연도나).
- 설치 프로그램 빌드(5개 연도 자동 포함): `dotnet publish SunnyToolsInstaller/SunnyToolsInstaller.csproj -c Release`
- 테스트 프로젝트/린터/CI 없음.
- 참고: 해당 연도 애드인이 Revit에 로드된 상태면 빌드의 마지막 DLL 복사 단계만 실패한다 (컴파일 자체는 실패하지 않으며, 그 연도 Configuration에만 영향).
