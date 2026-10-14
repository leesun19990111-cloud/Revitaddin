# 독립 매개변수 관리자

## 2026-10-14 · 0.1.0 첫 테스트 버전

사용자는 일람표 행 대응을 대신해 별도 외부 프로그램을 요청했다. 일반/중앙/로컬 RVT를 등록하여 매개변수를 직접 조회·일괄 입력하고 다른 모델의 동일 유형으로 값을 복사한다. **대상 RVT를 미리 열 필요가 없어야 한다**고 명시했다. 엑셀 분석·규칙은 나중에 한다는 마지막 지시를 따랐다.

### 파일과 실행 구조

- `SunnyParameterManager/Core`: 순수 DTO·동일 매개변수 연결·입력 검증·최신성 검사.
- `Desktop`: .NET 8 WPF 독립 실행 프로그램. Revit API를 참조하지 않는다. 선택·검색·미리보기·작업 저장·최종 승인.
- `Bridge`: Revit 2026.4 API 기준 연결 모듈. `WorkerApplication`이 환경변수로 인증된 **별도 빈 Revit 프로세스**에서만 타이머와 ExternalEvent를 만든다. 일반 Revit 시작에서는 아무 이벤트도 등록하지 않는다.
- `ModelEngine`: 일반 파일 열기 또는 파일 기반 중앙에서 새 로컬 생성 → 실제 매개변수 재검증 → TransactionGroup/Transaction → 결과값 재검증 → 저장 → 필요 시 중앙 동기화 → 닫기.
- `ParameterReader`: 실제 소유 요소의 Parameters를 열거한다. 인스턴스에서 공유 GUID getter의 유형 대리값을 쓰지 않는다. 작업세트만 누락 시 기본 getter로 보완한다. 모델 카테고리 유형/인스턴스 및 ProjectInfo를 조회한다.
- `Tests`: 55개 순수 Core/실제 WPF 인스턴스 검사 및 최소·일반 크기 화면 렌더. `LifecycleTests`: 실제 ModelEngine.cs를 링크하여 25개 API 대역 실패 분기 검사. Autodesk DB 자체의 행동을 검증하는 테스트가 아니다.
- `build-release.cjs`: 테스트 → Desktop publish → Bridge build → 기존 installer의 별도 제품 모드 publish → 페이로드/ZIP 해시 검증. `--verify`는 기존 패키지만 재검증한다.

### 과거 크래시 경계 — 반드시 유지

기존 `docs/namer/linked-edit-crash-v105.md`에서 금지한 호스트 내부 링크 해제·새 로컬 열기 경로는 **변경하지 않았으며 되살리면 안 된다**. 이 제품은 이번 사용자 요청에 따라 **별도 전용 프로세스**에만 자동 열기를 구현했다. 활성 사용자의 호스트/링크를 해제·재로드하지 않는다. 전용 프로세스에 어떤 문서라도 이미 있으면 요청을 중단한다. 실제 중앙 검증 전에는 안전성이 입증됐다고 쓰지 않는다.

UIApplication을 장기 보관하지 않는다. DB 호출은 ExternalEvent.Execute 안에서만 수행한다. 타이머는 파일 IPC와 Raise만 수행한다. `IFailuresPreprocessor`는 사용하지 않는다. 사용자 Revit과 전용 프로세스를 강제 종료하지 않는다. 다른 설치 애드인이 전용 Revit에서도 로드될 수 있으므로 별도 프로세스가 네이티브 크래시를 완전히 방지하는 것은 아니다.

### 안전 규칙

1. 읽기 작업은 값을 쓰거나 RVT 저장하지 않는다. 중앙 조회도 새 작업 로컬에서 실행하고 닫는다.
2. 실제 적용은 UI 명시 확인 필수. 기존 로컬은 다른 미동기화 작업 포함 경고/승인 필수. 다른 사용자의 로컬은 거부한다.
3. 실파일 경로 정규화, 중앙별 로컬 mutex, 문서 CreationGUID/WorksharingCentralGUID, Owner UniqueId, ParameterKey, 원시값·자료형·범위·단위·실제 권한을 확인한다. 조회 후 변경을 몰래 덮어쓰지 않는다.
4. 동일 중앙/파일은 UI에 중복 등록해 적용하지 않는다. shared GUID / builtin ID를 유지한다. 일반 매개변수는 명시 승인 + 유일한 이름/자료형/범위만 복사한다. 다른 shared GUID를 이름으로 연결하지 않는다.
5. 인스턴스 자동 대응은 없다. 사용자 선택 집합에 직접 입력만 허용. 모델 간 원시 ElementId는 복사하지 않고 이름과 대상 UniqueId 또는 작업세트 GUID를 재검증한다.
6. 전체 입력을 검증하고 단일 문서 묶음 트랜잭션에서 수정한다. 수정 후 요청값과 실제값이 다르면 묶음 롤백. 수식·실제 읽기전용은 해제하지 않는다.
7. 문서 변경 확정 이후 저장·동기화 실패는 열린 문서/로컬을 보존하고 나머지 모델 중단. 결과 불명 시 재시도·자동 닫기 금지. sync 성공 뒤 close만 실패하면 Success=true + RequiresAttention=true로 **이미 반영한 변경을 실패로 잘못 재시도하지 않게** 한다.
8. 소유권 강제 취득/일괄 반납 없음. 중앙 lock callback은 대기하지 않음. 다른 사용자의 로컬은 최신 내용 가져오기가 필요함을 안내한다. 다중 모델은 원자적 일괄 성공/롤백이 아니다.
9. 요청 인증·버전·Guid 검사 후 요청파일을 claimed로 옮겨 한 번만 소비한다. 응답 불명/45분 timeout이면 세션 잠금, 프로세스 강제 종료 없음.
10. 일반파일/기존 로컬은 변경 전 백업. 중앙은 별도 새 로컬을 유지하며 중앙 서버 백업과 같다고 안내하지 않는다. 기록/로컬에 실제 프로젝트 데이터가 있으므로 git에 넣지 않는다.

### 배포

별도 제품 버전 `0.1.0`, `SunnyParameterManagerInstaller_out_v010`, `SunnyParameterManager_Installer_v.0.1.0.zip`. 기존 Sunny Tools v109는 그대로 최신 애드인으로 유지한다. 별도 제품을 배포했다고 기존 애드인 v109를 아카이브하지 않는다. 다음 매개변수 관리자 배포 시 같은 제품의 이전 산출물을 `Old_Versions`로 옮긴다.

`SunnyToolsInstaller`의 ParameterManagerOnly 모드는 WallSplitter 5년 payload를 빌드·배포하지 않는다. `%LOCALAPPDATA%/SunnyTools/ParameterManager/App-0.1.0-<timestamp>-<id>` 버전 폴더 설치 + `%APPDATA%/Autodesk/Revit/Addins/2026/SunnyParameterManager.addin` + 바탕화면 바로가기. 기존 설치 폴더 삭제 없음. manifest/바로가기 이전본 보존. Autodesk DLL 배포 금지. 정상 Sunny Tools installer 경로는 유지한다.

Revit 2026/.NET8Desktop x64 설치가 필요하다. Autodesk Revit 엔진 없이 RVT를 직접 변경하는 SDK가 아니다. ACC/Revit Server 경로, 타 연도, 자동 업그레이드, 분리 모델, 시트/뷰, 엑셀·규칙은 미지원.

### 실제 테스트 필요

현재는 사용자 RVT/중앙을 열거나 수정하지 않았다. 설치도 실행하지 않았다. 별도 테스트 중앙을 대상으로 조회 → 텍스트 1건 → 재열기/중앙 확인 → 두 모델 → 소유권 충돌/서버 장애/기존 로컬 다른 변경을 순서대로 검증해야 한다. 로그인·애드인 허용 팝업은 사용자가 확인한다. 첫 릴리스를 운영 전면 배포로 소개하지 않는다.
