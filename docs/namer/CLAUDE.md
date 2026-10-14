# NAMER (이름 일괄 변경)

## 2026-10-14 v109 — 공통 매개변수 원본 안내 / 일람표 작업세트 확장

- ProjectInformationService의 Read/Snapshot은 doc.IsLinked에서 원본 로컬 미연결임을 명시하며 실제 원본의 읽기 전용 권한은 그대로 검사한다. NAMER 조회/저장/원본 찾기 경로를 변경하거나 잠금 해제하지 않았다. 네이머 링크 프로젝트 정보의 모든 실제 실패 원인이 해결됐다고 단정하지 말 것.
- 일람표 opt-in 작업세트 참조에 한해 Integer Set 및 문서별 UserWorkset GUID/이름 재검증을 추가했다. NAMER 프로젝트 정보의 요소 참조/전용 열거값은 기존대로 차단. 자세한 동작과 실제 일람표 진단은 schedule-editor v109 참조. 기존 안전장치·회귀 유지.

## 2026-10-14 v106 — 모든 모델 프로젝트 정보 진입점/표시 명확화

- 사용자 요청: 링크된 모델의 프로젝트 정보값도 네이머에서 수정. 조사 결과 v100부터 Collect→ReadRows→ProjectInformationService.Read 및 모델별 Apply 경로가 이미 존재했다. 미구현이라고 단정하거나 원본 파라미터 읽기 전용을 무시하지 않는다. 이번에는 통합 창의 `프로젝트 정보 · 모든 모델` 바로가기와 전용 열/안내를 추가하고 기존 링크 저장 경로를 회귀 검증했다.
- 바로가기는 모델/검색 필터를 전체로 바꾸고 프로젝트 정보 카테고리·값 전체 지정으로 이동한다. 이전 이름 편집 입력만 비우고 기존 누적 변경은 보존한다. 자동 전체 선택/적용은 하지 않는다. 일반 카테고리로 돌아오면 헤더/안내 복원. 프로젝트 정보에서는 재료 병합 체크박스를 숨기되 다른 카테고리의 누적 병합 의도는 보존한다.
- `소속 모델 / 정보 항목 / 현재 값 / 변경 후 값 · 미리보기`. 값 더블클릭, 드래그/Shift 체크, 열 너비 조절, 실시간 미리보기, 항목명/값 검색, 빈 값 `없음` 표시 및 실제 빈 문자열 저장은 기존 기능 유지. 실제 읽기 전용, 지원하지 않는 전용 참조/열거형은 여전히 차단한다.
- `모델 상태 / 제외 사유`는 열린 원본 경로가 없는 링크에 무조건 편집 가능이라고 하지 않고 원본 로컬을 같은 Revit에 먼저 열고 다시 실행하도록 안내한다. 준비 상태 표시는 수집 당시 정보이며 최종 적용 시 v105 사전 검사를 다시 수행한다. 열린 원본의 기존 작업 포함 동기화 확인/소유권 보존/자동 재열기 금지는 불변. 현재 모델 자동 저장·동기화 없음.
- `.codex-build/unified-v106` 235건(기존 221 + 신규 14), `link-safety-v106` 52건, `release-v106` 91건 = 총 378건 통과. 테스트용 링크 Run 대역도 링크 캐시가 아니라 RequireOpenTarget을 쓰도록 실제 v105 계약과 맞췄다. 현재+두 링크에 동일 프로젝트 정보 UniqueId/매개변수 ID를 배치해 최신 열린 원본 조회, 반복 링크 중복 제거, 각자 다른 값 반영, 링크 캐시 미변경, 미선택 보존, 원본 누락 전체 사전 차단을 확인했다. 실제 중앙 서버 동기화가 아닌 대역 검증이다.
- 실제 WPF 기본/최소 크기 렌더 확인 및 2023~2027 빌드 오류 0. 설치 후 별도 테스트 중앙의 로컬들을 같은 Revit에 열고 호스트에서 프로젝트 번호/이름을 모델마다 다르게 입력하여 적용·중앙 반영을 확인할 것. 운영 모델 자동 테스트는 수행하지 않았다.

## 2026-10-14 v105 — 링크 임시 로컬 재열기 네이티브 강제 종료 차단

- journal.0239/0240/0241에서 링크 해제 후 `LinkedModelEdits` 임시 로컬 로드 중 `SelectedPartitionsForEdit`→`decommitDocument`→`0xc0000005`를 확인했다. 실패 시점은 중앙 동기화 호출 이전이다. 엔진 내부 결함의 정확한 원인을 단정하지 않는다. 원본 저널/덤프/프로젝트 경로는 저장소에 넣지 않는다. [비식별 사후 분석 및 검증](linked-edit-crash-v105.md).
- **아래 v96~v104의 ‘원본이 없으면 새 로컬 생성/링크 해제/재열기/복원’ 규칙은 폐기한다.** LinkedModelEditService.Run은 같은 Revit에 이미 열린 검증된 원본/로컬만 처리한다. CreateNewLocal/OpenDocumentFile/Unload/UnloadLocally/Load/Close/강제 소유권 반납/선행 Save를 호출하지 않는다. try/catch로 네이티브 접근 위반을 막을 수 없으므로 자동 경로 자체를 되살리지 말 것.
- RequireOpenTarget은 읽기 전용 사전 검사다. 원본이 없거나 잘못된 상태면 안내 후 차단. UnifiedEditService.Preflight는 변경 있는 모든 소스만 먼저 검사하여 뒤 모델의 준비 부족 때문에 앞 모델만 변경되는 것을 방지한다. 일람표도 같은 검사를 호출한다. 항목별 stale 값/소유권/형상 검사는 여전히 실제 반영 직전에 수행하므로 다중 모델 원자성 보장은 아니다.
- 열린 원본의 기존 작업 포함 확인(기본 No), 그룹 종료 뒤 Sync, RelinquishOptions(false), 성공 뒤 로그, 실패 변경 보존/RetainChanges/외부 명령 Succeeded 규칙은 유지한다. 중첩 Run은 Interlocked로 차단한다. 동기화 실패 시 문서를 열어 두고 재시도·Save·Close·Reload를 하지 않는다. 현재 모델 자동 Sync 없음.
- 진단 기록: `%LOCALAPPDATA%/WallSplitter/Diagnostics/LinkedEdit-yyyyMMdd.log`, 작업 GUID/시각/단계만 기록한다. 모델 경로·이름·값은 제외, 기록 실패는 무시한다. `sync-start`와 `sync-completed` 구분. 네이티브 종료 후 finally의 실행을 가정하지 말 것.
- unified-v105 221(기존213+사전검사8), release-v105 91, link-safety-v105 52 = 364 통과. 기존 72 링크 검사 중 폐기된 자동 로컬 경로는 제거하고 열린 문서/새 차단 정책으로 재구성했다. 5개 연도 빌드 오류0, 설치 DLL에서 금지 API 이름 부재 검사. **실제 Revit/중앙 서버 재현 검증은 미실시**. 사용자가 테스트 중앙의 로컬을 먼저 열어 진행해야 한다.

## 2026-10-14 v104 — 통합 목록의 모델/이름 정렬

- UnifiedEditWindow 공통 검색 영역에 정렬 ComboBox. 기본 모델 기준: 현재 먼저→모델명→모델 키→패밀리→작업명. 이름 기준: 작업명(프로젝트 정보는 항목명 Family)→패밀리→현재 먼저→모델명. 마지막 Key로 동률을 결정하며 문화권 대소문자 무시 비교를 사용한다. 재료 지정은 재료명 아닌 대상 유형명을 기준으로 한다.
- Refresh는 기존 필터와 체크 해제 규칙을 그대로 사용하고 BuildDisplay로 정렬/계층을 구성한다. Resort는 인라인 편집 확정→드래그 종료·Shift 기준 초기화→현재 필터 결과만 재정렬한다. **정렬 전환 자체는 검색을 재실행하거나 체크를 지우지 않는다.** 누적 Working/NewMaterial·입력 미리보기·페이지 한도·스크롤·모델별 재료 선택을 보존한다. 정렬 설정은 열린 창 동안 유지하며 다음 창은 모델 기준으로 시작한다.
- 패밀리 자식은 Source.Rows+ParentId로 찾아 부모 바로 아래에 이름순으로 배치한다. 자식을 전 모델 이름순에 섞지 말 것. 하위 전체 선택은 페이지 밖 자식도 계속 포함한다. 현재 모델 전용 창/모델 수집·저장·중앙 동기화는 변경하지 않았다. UI 순서와 모델별 실제 반영 순서는 독립이다.
- unified-v104 기존199+정렬14=213, release-v104 91, link-safety-v104 72, 총376 대역 회귀 통과. 모델/이름 전환·검색 및 모델 필터·동일 ID 모델 분리·패밀리 계층·작업값/선택/페이지 유지·재료 대상명 정렬 확인. 기본/최소 창 렌더 확인, 2023~2027 빌드 오류0. 실제 Revit 라이브 검증은 미실시.

## 2026-10-14 v103 — 공통 매개변수 재검증의 기본 ID 접근 보완

- `ProjectInformationService.Validate`는 `id:<음수 built-in>`를 Parameters 열거뿐 아니라 get_Parameter(BuiltInParameter)로 다시 조회한다. 시트 번호처럼 전용 접근자는 지원하지만 열거에 없는 항목을 저장 직전에 잘못 거부하는 문제를 수정했다.
- 공유 GUID, 정의/저장형/원시값, writable 상태 검사는 유지한다. NAMER 프로젝트 정보의 참조 읽기 전용/일반 이름/재료 편집 범위는 변경하지 않았다. unified-v103 199 + release-v103 91 + link-safety-v103 72 회귀 통과. 세부 도면목록표 변경은 schedule-editor v103 참조. 실제 Revit/서버 검증은 미실시.

## 2026-10-14 v102 — 공통 매개변수 서비스의 일람표 참조 편집 확장

- `ProjectInformationValue`에 일람표 전용 참조 종류/원래·선택 UniqueId/모델별 후보/단위 라벨 추가. `UnifiedRow.Changed`는 해당 참조만 ID로 비교한다. 일반 이름·재료·프로젝트 정보의 기존 판단은 유지한다.
- `ProjectInformationService.Apply(schedule:true)`에서 명시적으로 활성화한 재료/레벨 참조만 대상 문서의 ID·이름·클래스 재검증 후 Set(ElementId). NAMER `Read`/`Snapshot`은 참조를 계속 읽기 전용으로 생성한다. 일람표만 ConfigureReference로 지원 필드를 opt-in한다. NAMER의 프로젝트 정보 참조까지 임의로 활성화하지 말 것.
- 기존 값·정의·단위·권한 사전검사, transaction 취소 및 성공 뒤 로그 규칙 유지. unified-v102 177, release-v102 91, link-safety-v102 72 회귀. 실모델/중앙 서버 검증은 별도 필요. 상세: `docs/schedule-editor/CLAUDE.md` v102.

## 2026-10-13 v100 — 프로젝트 정보 카테고리

- `NamerCategory.ProjectInformation`은 enum 끝에 추가(기존 순서/직렬화 값 유지). `CollectCandidates`는 이 분류에서 요소 이름을 반환하지 않는다. 프로젝트 정보는 한 요소의 여러 매개변수이므로 `ProjectInformationService.Read`가 별도 순수 값 행을 만든다. `UnifiedRow.Key`에 매개변수 ID/공유 GUID를 포함해 동명 항목도 분리한다.
- 통합 창에 9번째 카테고리와 `값 전체 지정`을 추가했다. 기존 이름 편집·드래그·열 크기·가족 계층은 그대로. 매개변수명/값으로 검색, 기존 값/미리보기, 빈 값은 표시만 `없음`(원시값은 빈 문자열). 직접 입력/일괄 지정 시 빈 텍스트 허용. 숫자/예 아니요는 형식 검사. 읽기 전용·요소 참조·전용 열거값은 표시하되 편집 차단.
- 기존 모드리스 창은 `ProjectInformationPanel`을 같은 창의 카테고리 영역에 표시한다. 다른 카테고리의 미적용 이름 변경은 유지한다. 패널은 Revit 객체 없이 순수 값만 사용. `ExternalEvent`에서 프로젝트 정보와 누적 이름 변경을 TransactionGroup으로 처리하고 하나라도 실패하면 이번 묶음 전체 취소. 로그는 그룹 성공 이후만 기록. `Raise` 거절/예외/실행 완료에서 창 비활성 상태를 복구한다.
- 적용 직전 대상 UniqueId/매개변수 GUID 또는 ID/정의명/저장형/데이터형/내부 원시값/표시 단위를 재검사한다. 외부 변경을 덮어쓰지 않는다. 문자열 `Set`, 정수/예 아니요 `Set(int)`, 단위 실수 `SetValueString`. IFailuresPreprocessor를 추가하지 않았다.
- `ProjectInformationService.Snapshot/Validate/Apply`는 일람표 에디터에서도 재사용하므로 수정 시 `docs/schedule-editor/CLAUDE.md`도 읽는다. `Validate(forWrite:false)`는 정렬/필터 의존값 검사에만 사용하며 실제 쓰기는 항상 writable 검사.
- 실제 소스+API 대역 테스트 unified-v100: 기존 64 + 프로젝트 정보/검색 23 + 일람표 27 = 114개. 기존 계층/재료 91개 및 링크 안전 72개 별도 유지. 실모델/서버는 미검증.

## 2026-10-13 v99 — 같은 Revit에 열린 링크 원본 재사용

- 사용자 명시 요청으로 v96의 “같은 중앙/로컬이 열려 있으면 차단”을 대체한다. FindOpenTarget은 Application.Documents의 비링크·비패밀리 문서를 실제 파일/중앙 경로로 찾고 중복 후보는 거부한다. 읽기 전용/편집 중/분리/비작업공유/중앙 연결 불일치 문서는 새 로컬로 우회하지 않는다. 호스트 자기 자신은 현재 모델 항목으로 유도한다. WPF에는 Document를 저장하지 않는다.
- UnifiedEditService.Collect는 열린 원본이 있으면 그 문서의 현재 이름·재료·인스턴스 범위를 읽는다. 링크의 오래된 스냅샷 때문에 미저장 이름과 충돌하지 않도록 한다. 모델별 최종 확인에 열린 문서 경로를 넣고, 실제 반영 직전 **항상 별도 기본 No 확인**으로 기존 미저장/미동기화 작업까지 함께 동기화됨을 알린다. IsModified=false여도 저장된 미동기화 작업이 있을 수 있으므로 확인을 생략하지 않는다.
- 재사용 분기에는 CreateNewLocal/OpenDocumentFile/Unload/Reload/Close/선행 Save가 없다. 해당 문서의 TransactionGroup으로 이번 변경만 묶고 모든 그룹 종료 후 SynchronizeWithCentral을 호출한다. 로컬은 SaveLocalBefore/After=true, 중앙 직접 열기는 false(공식 API에서 중앙에는 무시됨). RelinquishOptions(false)로 기존 사용자 소유권을 유지하며 취소/실패 cleanup에서도 전체 소유권을 반납하지 않는다. 새로 빌린 소유권도 사용자가 나중에 반환한다. 중앙 파일 덮어쓰기/SaveAs/강제 체크아웃 금지 유지.
- 열린 원본에는 링크 해제가 필요 없어 다중 호스트 로드·호스트 중앙 직접 열기 검사를 우회할 수 있다. 원본이 없을 때의 전용 로컬 분기는 기존 제약을 유지한다. 재사용 시 링크 표시 갱신은 자동 수행하지 않고 결과창에서 별도 안내한다(다중 호스트 Reload 제약, 열린 원본 및 Undo 보호). 중앙 성공과 링크 표시 갱신은 같은 의미가 아니다.
- 적용 중 실패는 이번 그룹만 롤백한다. 동기화 실패는 열린 문서/변경을 남기고 자동 저장 재시도·닫기·소유권 반납 없이 실제 문서 경로를 안내한다. RetainChanges는 성공 여부(Synced)와 별개다. **외부 명령의 Cancelled/Failed는 Revit 변경을 되돌리므로**, 이미 중앙 동기화했거나 열린 문서에 복구 변경을 보존할 때는 외부 명령에 Succeeded를 반환하고 실제 실패/미처리는 결과창으로 보고한다. 여러 모델 중 실패 시 남은 모델 중단은 유지한다. 로그는 중앙 성공 뒤에만 기록.
- 공식 근거: [중앙 동기화 옵션](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/96eaf3af-971d-da6d-a857-88d6e602ffd4.htm), [외부 명령 반환과 롤백](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-API/files/Revit_API_Developers_Guide/Introduction/Add_In_Integration/Revit_API_Revit_API_Developers_Guide_Introduction_Add_In_Integration_External_Commands_html.html). 실제 서버/Revit 동기화는 미검증이며 unified-edit-testing.md의 v99 항목으로 별도 테스트 중앙에서 확인해야 한다.
- 검증: `.codex-build/link-safety-v99` 실제 공통 서비스+대역 72개(열린 로컬/중앙, 필수 확인, 다중 호스트, 취소/적용/동기화 실패, 문서·소유권 유지, 외부 명령 반환), `unified-v99` 64개(열린 원본 이름/재료 조회 포함), `release-v99` 기존 91개, 총 227개. Revit 2023~2027 오류 0개. 대역 테스트는 실제 Revit 엔진·소유권·중앙 서버 쓰기 검증을 대신하지 않는다.

## 2026-10-13 v98 — 통합 창 UI 회귀 복원 / ModelPath 형식 오판 수정

- 사용자 실측: v97 통합 창에서 영역 구분/열 조절/입력 즉시 미리보기/드래그 선택이 사라짐. UnifiedEditWindow에 검색·변경 설정 테두리 구역, 헤더 GridSplitter 3개와 ActualWidth→모든 행 동기화, TextChanged 기반 미리보기, 행 캡처 드래그/Shift 범위를 복원했다. 부모·자식 체크 독립 및 하위 유형 전용 체크는 유지한다. 드래그는 시작 때 선택/해제 상태를 고정하고 빠르게 건너간 사이 행도 포함한다. 체크 변경 시 행 트리를 다시 만들지 않는다.
- 미리보기 문자열과 UnifiedRow.Working은 분리한다. 선택한 행에만 변환을 미리 표시하고 Stage만 누적한다. 최종 적용은 미리보기 계산을 호출하지 않는다. 기존/변경 이름 더블클릭은 변경 이름 칸에 편집기를 띄우며 첫 클릭 체크 반전을 복원한다. Enter/포커스 이탈/다른 곳 클릭 확정, Esc 취소. 인라인 편집 중에는 미리보기를 편집값으로 저장하지 않는다. Render는 페이지 한도 및 스크롤을 보존한다. 창은 여전히 순수 값 기반 모달이고 기존 모드리스/네이티브 특성은 현재 모델 전용 도구로 유지한다.
- 첨부 오류는 ValidateHostLocal의 `central is not FilePath` 분기였다. journal.0234의 파일 기반 로컬/중앙 경로 기록과 대조했다. **GetWorksharingCentralModelPath 등은 파일 기반이어도 기본 ModelPath를 반환할 수 있으므로 구체 CLR 타입을 판정에 쓰지 말 것.** IsFileBasedPath는 null/Empty/ServerPath/CloudPath 속성으로 검사하며 호스트 검증, 링크 조회/중복 제거, 원본 재개방 검증, 열린 문서 중앙 비교에 공통 적용한다. 실제 파일의 존재/읽기 핸들 정규화/중앙-로컬 다른 경로/분리 문서/버전/소유권 안전 검사는 유지한다. 공유폴더 UNC는 Revit Server가 아니다.
- 근거: 설치 RevitAPI.xml 및 [ModelPath API](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/40a84c72-e4b8-72ac-2f71-3216c66a11b3.htm). 링크는 CreateNewLocal→편집→Save→SynchronizeWithCentral→로그→복원으로 중앙 원본에 반영한다. 로컬 저장 성공만으로 중앙 성공 처리하지 않는다. 현재 프로젝트의 기존 미저장 변경을 자동 동기화하는 기능은 추가하지 않았다.
- `.codex-build/unified-v98` 실제 UI/서비스 + 대역 검사 60개(신규 16개: 이벤트 연결, 입력 미리보기와 작업값 분리, 열 동기화, 드래그·범위, 직접 편집, 페이지·스크롤). `link-safety-v98`은 기본 ModelPath를 반환하도록 대역을 교정해 46개, 기존 창·재료·레이어 91개, 총 197개. 기본/최소 크기 렌더 확인. 실제 OS 마우스/Revit 서버 동기화를 검증한 것은 아니다. 기존 대역이 항상 FilePath를 반환해 이번 라이브 버그를 놓쳤으므로 기본 래퍼 대조 검사를 없애지 말 것.

## 2026-10-13 v97 — 현재 모델과 모든 링크의 통합 목록

- 사용자 정정: 파일별 창이 아니라 한 일람표 같은 목록에서 현재 모델과 링크 모델을 동시에 검색·선택·변경한다. v96 Choose 분기를 명령 진입점에서 제거했다. 링크가 있으면 UnifiedEditService/UnifiedEditWindow로 진입하고 없으면 기존 모드리스 창이다. 통합 창의 현재 모델 전용 도구로 기존 창에 접근한다. 기존 NAMER가 열려 있으면 미적용 작업 보호를 위해 먼저 닫도록 안내한다.
- UnifiedSource/UnifiedRow는 UI용 값 스냅샷이다. 창은 Document/Element/UIApplication을 보관하지 않는다. 키는 소속 모델 키 + UniqueId이고, 재료는 슬롯 종류/레이어/매개변수 ID까지 포함한다. 링크 조회 중 얻은 객체를 링크 해제 후 사용하지 않는다. 적용 직전에 전용 새 로컬에서 UniqueId로 다시 찾고 이름/재료/슬롯/인스턴스 범위를 비교하며 오래된 조회 값은 덮어쓰지 않는다.
- 현재 + 최상위 링크를 읽고 동일 원본 반복 배치는 실제 파일 경로 정규화로 중복 제거한다. 직접 링크를 중첩보다 먼저 수집한다. 미로드/지원 제외 링크는 모델 상태에 사유를 남긴다. 중첩/클라우드 등이 읽히더라도 편집 불가로 표시한다. 조회만으로 로컬 생성/링크 해제/동기화를 하지 않는다.
- 통합 창은 모달이며 소속 모델/패밀리/기존 이름/변경 이름 열, 모델 필터, 검색 6모드, 기존 4종 이름 연산, 직접 입력을 제공한다. 패밀리 하위 유형 선택은 모델별 부모 UniqueId로 연결하고 Type 목록과 같은 row 상태를 쓴다. 전체 선택은 펼친 검색 결과(페이지 밖 포함), 해제는 접힌 유형 포함. 200개 페이징. 기존 현재 모델 모드리스/특성 창은 유지한다.
- 일괄 최종 확인 한 번(기본 No) 후 변경 있는 링크만 순차 Run(batch report) 실행, 현재 모델은 마지막에 트랜잭션 반영한다. 현재 모델은 기존 미저장 변경까지 동기화하지 않도록 자동 저장/Sync하지 않는다. 링크 성공/실패/복원 경고/미처리를 모두 보고하며 실패 또는 복원 경고 시 나머지는 중단한다. **여러 중앙 간 원자적 롤백이 아님**. 성공한 앞 모델까지 취소됐다고 표현하지 말 것.
- v96 로컬 오판 수정: BasicFileInfo.IsLocal은 새 로컬 첫 저장 전 false일 수 있으므로 단독 차단을 제거했다. ValidateHostLocal은 IsDetached, 저장 경로, 파일 기반 중앙 연결, 실제 중앙/로컬 경로 동일 여부를 검사한다. CanonicalPath는 읽기 공유 핸들의 GetFinalPathNameByHandle로 매핑 드라이브/UNC 경로를 정규화하고 실패하면 안전하게 중단한다. 사용자 모델을 판정용으로 저장하지 않는다.
- 실제 통합 Window/Service/Model + Revit 대역 검사 44개, 실제 링크 서비스 40개, 기존 창·재료·레이어 회귀 91개(총 175개) 통과. 순차 처리 중 실패/취소, 문서 ID 충돌, 동일 원본 중복, 검색/선택/페이징, 오래된 원본값 차단, 인스턴스 범위 변경을 검사했다. 기본/최소 크기 렌더 확인. **실제 Revit 공유 서버 쓰기 검증은 미실시**, 테스트 계획은 unified-edit-testing.md.

## 2026-10-13 v96 — 공유폴더 링크 중앙모델 원본 편집

- 사용자 확정: 링크 원본도 변경/저장하며, 공유 서버의 파일 기반 중앙모델이다. NAMER/재료 지정의 공통 `LinkedModelEditService`가 현재 프로젝트 또는 최상위 링크 한 파일을 선택하게 한다. GetLinkDocument는 읽기 전용이므로 절대 수정하지 않는다.
- 원본 경로·현재 버전·중앙 여부·이미 열린 같은 중앙/로컬·다중 문서 로드·호스트 로컬 여부 등을 검사한다. 시작 확인은 기본 No. CreateNewLocal → 사용자별 링크 해제(UnloadLocally; 비작업공유 호스트만 Unload) → DoNotDetach로 전용 로컬 열기 → 편집 → 최종 기본 No 확인 → TransactionGroup → 로컬 저장 → 중앙 동기화 → 링크 복원 순서다. 파일 복사/SaveAs로 중앙을 대체하지 않는다. 클라우드/Revit Server/중첩/미로드 링크는 지원하지 않는다.
- **모드리스 규칙의 의도된 예외**: `linkedModal:true` 창만 ShowDialog로 실행한다. 별도 로컬 수명 동안 다른 Revit 작업을 막고, LinkedRenames/LinkedMerge를 명령에 돌려준다. 호스트 ExternalEvent에 링크 ElementId를 보내지 않는다. static Instance를 대체하지 않으며 네이티브 특성 버튼도 막는다. 기존 현재 프로젝트 창은 계속 모드리스다. 이미 열린 NAMER가 있으면 먼저 닫도록 안내한다.
- 기존 ApplyRenames에 선택적 deferredLogs를 추가했다. 링크는 항목 실패 시 외부 TransactionGroup을 전체 롤백하며, 로그는 중앙 동기화 성공 이후에만 남긴다. 기존 현재 프로젝트의 부분 성공 동작은 바꾸지 않았다.
- 소유권을 강제로 가져오지 않는다. 서버 잠금 대기는 자동 반복하지 않는다. 동기화 실패 시 중앙 반영 여부가 불확실하므로 자동 재실행 금지 안내와 복구 로컬 경로를 남긴다. 로컬 저장이 두 번 실패하면 미저장 문서를 닫지 않는다. 취소/롤백 때 빌린 소유권 반환을 시도하고 실패를 알린다.
- Undo 기록 소실 가능성, 동기화 후 호스트 Undo 불가, 공유 원본의 다른 사용처 영향은 두 확인창에 안내한다. 링크 갱신 실패와 중앙 동기화 성공은 구분한다. 전용 로컬은 자동 삭제하지 않는다.
- 실제 Window 대역 검사 91개 중 링크 모달 6개; 공통 서비스 대역 안전 분기 33개. 2023~2027 컴파일 통과. **실제 서버/Revit 통합 테스트는 미실시**이며 별도 테스트 중앙+호스트에서 확인해야 한다. 연결된 운영 중앙의 로컬 복사본으로 테스트하지 말 것.

## 2026-10-06 v95 — 재료 병합 시 복합 구조 보존

- `RedirectMaterialUsages`의 복합 레이어 재료 교체는 `SetLayers` 대신 기존 구조의 `SetMaterialId(layerIndex, newMaterialId)`를 사용한다. SetLayers는 구조 재료 지정 등을 초기화하므로 재료만 교체할 때 쓰지 않는다. 구조 재료·가변 레이어·기능/우선순위·두께·코어/데크 설정을 그대로 둔다. 배열 인덱스와 LayerId를 혼동하지 말 것.
- 재료 지정에도 같은 문제를 수정했다(`docs/material-assign/CLAUDE.md` v95). 기존 병합 범위·삭제·재생성·트랜잭션 정책은 변경하지 않았다. 5개 연도 빌드와 금지 호출 재발 검사 통과, 실제 Revit 병합 후 유형 편집 확인은 필요하다.

## 2026-10-06 v94 — 펼친 패밀리의 하위 유형만 전체 선택

- 펼친 부모 행 바로 아래에 `하위 유형만 전체 선택 (선택/전체)` 체크박스를 추가한다. 해당 패밀리의 유형 ID만 조작하므로 부모 체크와 다른 패밀리의 선택을 건드리지 않는다. 200행 페이지 밖 유형도 포함한다.
- 일부 선택 상태에서 누르면 전부 선택하고, 모두 선택된 상태에서 누르면 전부 해제한다. 개별 체크·드래그·전체 선택/해제에도 집계가 갱신된다. 클릭 이벤트를 써 프로그램으로 집계 상태를 바꾸는 일이 역으로 선택을 바꾸지 않게 한다. 그룹 일괄 변경 동안 집계를 잠시 억제해 반복 계산을 줄인다.
- 그룹 체크박스는 RenameRow 태그가 없는 별도 컨트롤이다. 이름 편집/행 드래그로 부모가 잘못 체크되지 않는다. 접을 때 그룹 컨트롤은 제거하지만 기존 선택 유지 규칙은 v93과 같다.
- 실제 코드 연결 하네스 `.codex-build/release-v94`에서 v93 검사 35개와 그룹 전체 선택/해제·부모 제외·페이지 밖·부분 집계 5개(총 40개)를 통과했다. 실제 마우스 입력은 별도 Revit 확인이 필요하다.

## 2026-10-06 v93 — 패밀리 검색 결과의 하위 유형 펼치기/접기

- 사용자 요청: 유형 이름이 제각각이어도 패밀리 이름으로 검색해 소속 유형들을 열고 닫으며 선택·이름 변경할 수 있게 한다. 기존 Family 카테고리의 로드 가능한 패밀리만 계층화한다. 시스템 패밀리 유형은 기존 Type 카테고리를 그대로 쓴다.
- `CollectCandidates(Family)`는 **Family만 반환하는 계약을 유지**한다(ChangeReplayEngine도 공유). 창 내부 `RefreshFamilyTypes`에서 `Family.GetFamilySymbolIds()`로 소속 유형을 수집하며, 이름 문자열로 부모를 추측하지 않는다.
- 목록 세 층: `_categoryElements`=검색할 부모, `_filteredElements`=검색된 부모+모든 자식(편집/해제 범위), `_displayElements`=부모+펼친 자식(200행 페이지/전체 선택 범위). 검색 모드 6개는 부모의 작업 중 이름에만 적용한다. 자식은 검색어와 무관하게 전부 포함한다.
- 패밀리 체크와 유형 체크는 **독립**이다. 사전 선택도 기존대로 부모만 체크하고 자식 전체를 자동 선택하지 않는다. 전체 선택은 펼쳐진 목록 전체(더 보기 뒤 항목 포함)만 새로 체크하고, 전체 해제는 현재 검색된 부모의 접힌 자식까지 해제한다. 접기는 선택을 지우지 않으며 상태 줄에 접힌 유형의 선택 개수를 표시한다. 검색에서 제외되면 해당 부모와 자식의 체크를 해제하지만 이미 적용한 편집값은 유지한다.
- `_workingNames/_trueOriginalNames/_checkedIds`는 ElementId 단위로 유형 탭과 공유한다. 적용은 검색 범위의 체크된 부모/자식을 편집하며 최종 적용은 기존처럼 누적 diff만 ExternalEvent로 전달한다. 행에 Category를 저장해 하위 유형의 특성 요청은 Family가 아니라 **Type**으로 전달한다.
- 펼침 상태는 ID로 유지한다. 카테고리 재진입/최종 적용 후 자식 목록을 갱신하고, 문서 변경 시 펼침/선택/작업값을 전부 비운다. 목록 재생성 전 인라인 편집을 확정한다. 다른 문서로 바뀌는 경우에는 **문서 대입 전에 인라인 편집을 취소**해 동일 숫자 ID의 다른 요소에 편집이 넘어가지 않게 한다.
- 폭 조절 시 계층 기호 폭을 이름 칸에서 차감해 변경 후 이름 열을 맞춘다. 필터 안내는 줄바꿈하고 누적 변경 상태도 잘리지 않게 줄바꿈한다. 새 펼침 및 전체 선택/해제/더 보기 이벤트는 Guard로 보호한다. UIApplication 저장/창의 문서 이벤트 구독/커스텀 IFailuresPreprocessor는 추가하지 않는다.
- 검증: `.codex-build/release-v93/NamerSmoke.csproj`가 **실제 NamerWindow 코드와 XAML/Theme를 링크**하고 Revit 데이터/API만 대역으로 교체해 검색 6모드, 빈 검색, 독립 선택, 245개 자식 페이징, 접힌 체크/편집 유지, 범위 제외 해제, 직접 편집, 유형 탭 공유, 최종 요청 중복 방지, 문서 전환 격리를 확인한다. 실제 Revit 트랜잭션/네이티브 특성 창 및 마우스 입력은 이 하네스로 검증되지 않으므로 설치 후 라이브 확인이 필요하다.

`NamerWindow.xaml(.cs)`/`NamerCommand.cs`/`NamerPropertiesWindow.xaml(.cs)`를 건드리기 전에 이 문서를 읽을 것.

**주의: 아래 문단의 "modal" 설명은 2026-09-21에 뒤집혔다 - 이 문서 맨 아래 "모드리스 전환" 절을 먼저 볼 것.**

A batch-rename tool for Views/Sheets/Families/Types/Materials, added 2026-07-10. `NamerWindow` was **modal** (`ShowDialog()`), not modeless — a one-shot batch operation doesn't need live two-way sync, so `NamerCommand.Execute` can just run a `Transaction` directly after the dialog returns (still inside the valid API context, since `ShowDialog()` blocks synchronously within `Execute`) with **no `ExternalEvent` needed at all**. This is deliberately the simpler architecture; don't "upgrade" it to a modeless pattern unless a real requirement (e.g. live preview against concurrent Revit edits) demands it.

- **Item list**: deliberately built the same way as `SettingsWindow`'s token list — a `StackPanel` of manually-constructed rows (`CheckBox` + old-name `TextBlock` + new-name `TextBlock`), *not* `ListBox`/`DataGrid`/`ListView`. **Resizable name columns** (added 2026-07-16, on request — long 유형/패밀리 names were truncated with "..." and couldn't be widened): a header `Grid` above `ItemsPanel` has two named `ColumnDefinition`s (`OldNameColumn`/`NewNameColumn`) with a named `GridSplitter` (`NameColumnSplitter`) between them; since rows are plain `StackPanel`s with no column concept of their own, each row's name/new-name `TextBlock` gets its `Width` copied from the matching column's `ActualWidth` at render time, then kept in sync by `NameColumnSplitter_DragDelta` (fires continuously while dragging) calling `UpdateLayout()` then re-writing every already-rendered row's `Width`. **Two other approaches were tried and live-tested broken first**: a plain `ElementName`+`ActualWidth` `Binding` never updated the rows at all when the splitter moved (only `System.Windows.Data.Binding` compiles — `Binding` alone is ambiguous with `Autodesk.Revit.DB.Binding`, same class of collision as the `Visibility`/`Grid`/`Control`/`Color` gotchas above); `DependencyPropertyDescriptor.FromProperty(ColumnDefinition.ActualWidthProperty, ...)` doesn't even compile in this WPF version (no such public static field) — `ActualWidth` changes on `ColumnDefinition`/`RowDefinition` are a known WPF blind spot for ordinary change-notification mechanisms, hence going straight to the splitter's own `DragDelta` instead.
- **Filter mode combo** (`FilterModeCombo`, added 2026-07-13): the filter textbox alone only ever meant substring-contains; a `ComboBox` next to it (포함됨/포함하지 않음/일치함/일치하지 않음, `SelectedIndex` 0–3) now also supports contains-negated and exact-match(-negated), all case-insensitive via `WorkingNameOf(el)` compared/matched against the filter text in `RenderRows()`. An empty filter textbox always shows everything regardless of mode (including the "않음" modes) — a blank search has no criterion to negate, so treating it as "exclude everything" would be actively wrong. `FilterModeCombo_SelectionChanged` guards on `ItemsPanel == null` for the same reason `ModeRadio_Checked` guards on `ReplacePanel == null`: the ComboBox's XAML-default `SelectedIndex="0"` fires `SelectionChanged` synchronously during `InitializeComponent`, before Row 4's `ItemsPanel` field is connected, so calling `RenderRows()` unguarded there would NRE. **`~로 시작하는`/`~로 끝나는`** (added 2026-07-16, on request): indices 4/5, `string.StartsWith`/`EndsWith` with the same `CurrentCultureIgnoreCase` comparison as the other four modes — added identically to all three filter-mode combos in this codebase (NAMER's `FilterModeCombo`, and 재료 지정's `FilterModeCombo`/`DelFilterModeCombo` in `MaterialAssignWindow`, see `docs/material-assign/CLAUDE.md`), since all three share the exact same 4-mode `switch` shape that this just extends to 6.
- **Pagination, not virtualization** (root-caused 2026-07-10 after "so slow manual renaming is faster" live feedback): plain `StackPanel` has no UI virtualization, so building a row (4 WPF elements incl. two `TextTrimming`-enabled `TextBlock`s) for *every* matching element up front is the dominant cost — catastrophic for "유형" (every `ElementType` in the doc, easily thousands, no category pre-filter) and compounded further because `FilterBox_TextChanged` used to call the same full `RenderRows()` on *every keystroke*, so typing a filter re-paid that full cost per character. Fixed by capping each render to `PageSize` (200) rows via `RenderMoreRows()` + a "더 보기" button appended after the last row; `RenderRows()` (on category/filter change) now only computes `_filteredElements` (cheap, no UI) and renders the first page, so cost is bounded regardless of category/filter size. `SelectAllButton_Click`/`SelectNoneButton_Click`/`UpdateCountText` operate on `_filteredElements` (the full filtered set), not `_rows` (only the rendered page), so "전체 선택" still reaches items beyond the current page even though they have no on-screen row yet — they'll pick up the correct checked state whenever "더 보기" renders them (`IsChecked = _checkedIds.Contains(el.Id)` at row-creation time).
- **Filtering out an item also unchecks it** (added 2026-07-10, explicit request — "checked" must always mean "currently visible under the filter," never a stale cache from before the filter changed): `RenderRows()` now removes from `_checkedIds` any id that belongs to the *current category* (`_categoryElements`) but isn't in the newly computed `_filteredElements`. Scoped to the current category only, so checked items in other categories (persisted across category switches, per the two-stage-apply design above) are untouched. This runs on every filter keystroke, so narrowing/widening the filter text can silently drop items from the pending selection — expected, not a bug.
- **Drag-to-toggle checkboxes**: each row's `CheckBox` has `IsHitTestVisible=false` and is purely a visual indicator — all click/drag input is handled on the row's `StackPanel` instead (`Background=Brushes.Transparent` set explicitly, since a `null` Background makes a panel's empty space not hit-testable), tagged with its `RenameRow` via `.Tag`. Mouse-down on a row captures the mouse on `ItemsPanel`, computes a single "drag target state" (opposite of the clicked row's current state) once, then every row the cursor passes over during the drag (found via `VisualTreeHelper.HitTest` + walking up to the nearest ancestor with a `RenameRow` tag) gets *set* to that same target state — never toggled relative to its own state, so revisiting a row mid-drag doesn't flicker it back and forth. This is the same reason a plain click still works correctly: it's a drag of length zero, one row set once.
- **Four operations**, computed uniformly by `ComputeNewName(oldName)`: 문자열 치환 (`string.Replace`), 위치에 삽입 (1-based position → `string.Insert` at `position-1`, clamped to name length), 구분자 기준 자리바꾸기 (split at the *first* occurrence of the delimiter, swap the two sides around it — `"ABC-CBA"` with delimiter `"-"` → `"CBA-ABC"`), 구간 삭제 (1-based inclusive start/end position → `string.Remove`, e.g. start=6/end=9 deletes the 6th–9th characters; end clamped to name length, no-op if start is past the end of the name).
- Categories (`View`/`Sheet`/`Family`/`Type`/`Legend`/`Schedule`/`Material`) all collected via `FilteredElementCollector` and treated uniformly as `Element` — `Name` is get/set on `Element` itself (confirmed present and settable on `Element`/`View`/`ViewSheet`/`Family`/`ElementType` via `MetadataLoadContext` reflection before writing any code), so no per-category casting is needed anywhere except the collector queries themselves. `Legend` is *not* a separate Revit class — confirmed via reflection there's no `ViewLegend` type; a legend is a plain `View` with `ViewType == ViewType.Legend`. `Schedule` collects `ViewSchedule` (a `TableView`, itself ultimately a `View`). Since both are `View` subtypes, the generic `View` category explicitly excludes `ViewSchedule` and `ViewType.Legend` (alongside the existing `ViewSheet`/`IsTemplate` exclusions) so nothing appears in two categories at once.
- **`ViewTemplate` category** (added 2026-07-21, on request): a view template is not a separate Revit class — it's a plain `View` with `IsTemplate == true` — so `NamerCategory.ViewTemplate` collects `FilteredElementCollector(doc).OfClass(typeof(View)).Where(v => v.IsTemplate)`, the mirror-image filter of the existing "뷰" category (`Where(v => !v.IsTemplate && ...)`), keeping the two mutually exclusive. `Name` is get/set directly on `View` like every other category here, so `NamerCommand`'s generic `el.Name = newName` needed no changes; `ResolvePreSelectionForCategory`'s `default` branch (already shared by View/Sheet/Legend/Schedule) covers it too, since a pre-selected view template's own id is already the list item.
- **`Material` category** (added 2026-07-16, on request): `Material` collects `FilteredElementCollector(doc).OfClass(typeof(Material))` — `Material` is a plain `Element` (not an `ElementType`, so it's absent from the `Type` category's `WhereElementIsElementType()` query), but `Name` is still get/set directly on it, so it slots into the existing uniform-`Element` design with just a new collector case; no changes needed anywhere else (`NamerCommand`'s rename `Transaction` already operates on bare `Element.Name`). `ResolvePreSelectionForCategory` needs no `Material`-specific case either — it falls into the same `default: resolved.Add(el.Id)` branch as View/Sheet/Legend/Schedule, since a pre-selected Material is already the exact element the category list shows (there's no instance→type indirection to resolve, unlike Type/Family).
- **Material rename duplicate-name policy** (added 2026-07-16, on request): renaming a `Material` to a name another `Material` already has would otherwise just fail (Revit requires materials to have unique names) — `NamerWindow` shows a `MaterialDuplicatePanel` (visible only when category is `Material`, radio-toggled, default "병합"; a vertical `StackPanel` — heading line + one `RadioButton` per line, no parenthetical explanation text, per explicit request after the original single-line layout with inline descriptions felt cramped) right under the category radios, and `NamerCommand.RenameMaterial` checks for a same-name `Material` at commit time: if `MergeDuplicateMaterials` is true, `RedirectMaterialUsages` re-scans **every** `ElementType` (not just `MaterialSlotFinder`'s one-slot-per-type view used by the 재료 지정 tool) for **every** compound-structure layer or Material-spec parameter pointing at the old material, repoints each to the existing one, then deletes the now-orphaned old material; if false, `FindUniqueMaterialName` appends `1`, `2`, … (bare digits, per explicit request — not `GetOrCreateSingleLayerWallType`'s `(2)`/`(3)` style) until a free name is found. Scoped to `ElementType`-level usages only — per-instance material overrides are not redirected (known limitation, consistent with `MaterialSlotFinder`'s existing type-only scope). **Confirmed live bug, fixed twice (2026-07-16)**: merge success/failure was originally judged by whether `Document.Delete(material.Id)`'s returned `ICollection<ElementId>` contained `material.Id` — live-tested false-negative (redirect + delete both genuinely succeeded, old material visibly gone, usages correctly moved) yet the returned collection didn't reflect that, so a "병합 실패" warning fired despite total success. First fix (checking `doc.GetElement(material.Id) == null` directly instead of trusting the returned collection) turned out insufficient by itself — live-retested still false-negative immediately after `SetCompoundStructure`/`Parameter.Set` + `Delete` in the same transaction, apparently because Revit hadn't fully regenerated those changes yet at query time. Second fix: call `doc.Regenerate()` right before the `GetElement` check to force those pending changes to fully settle first. If the warning still legitimately fires after this, it likely means the material has a real usage this tool doesn't scan (Object Styles, fill patterns, appearance assets — outside `RedirectMaterialUsages`'s `ElementType`-only scope), not a false positive.
- Pre-selection support mirrors `SplitWallCommand`, with two live-bug fixes on top (2026-07-10, found from an actual bad rename that hit unrelated wire/material types across the whole document — see below):
  - **Instance→type/family resolution** (`ResolvePreSelectionForCategory`): what the user selects in Revit before opening NAMER is almost always model *instances* (a wall, a family instance), not the `ElementType`/`Family` objects themselves — those instance ids never literally match anything in `_categoryElements` for the Type/Family category. Resolve via `element.GetTypeId()` for Type, and `FamilyInstance.Symbol.Family.Id` / `FamilySymbol.Family.Id` for Family, before comparing.
  - **No-match fallback is now "check nothing", not "check everything"**: the old fallback ("if nothing pre-selected matches, check every element in the category") combined with the instance/type mismatch above meant selecting ordinary model elements and switching to "유형" silently checked *every* `ElementType` in the entire document — including completely unrelated disciplines (electrical wire/conductor types, materials) — which then got renamed for real on "최종 적용". Confirmed live via a screenshot showing `Steel`/`Copper`/`THHN`/`RHW-2`-style wire-type names mangled by an unrelated insert-at-position-5 operation. This default-check logic only runs the *first* time each category is loaded in a given window session (`_categoriesInitialized`).
- **`NamerCommand`'s rename `Transaction` deliberately sets NO custom `IFailuresPreprocessor`** — it originally had one (`SilentWarningsPreprocessor`, the same `DeleteWarning`/`ResolveFailure` pattern as `SplitWallCommand.SilentFailuresPreprocessor`, added to suppress unrelated pre-existing warnings surfaced by the full-document regenerate that any `Transaction.Commit()` triggers), but this caused a **confirmed live bug (2026-07-10): renames silently never reached the model** — no error dialog, Revit's status-bar progress gauge ran normally, but the wall/type names were unchanged after commit. Root-caused by checking `Transaction.Commit()`'s return value (previously ignored): it was returning `TransactionStatus.RolledBack` with zero exceptions thrown. Diagnostic instrumentation (counting `PreprocessFailures` calls) showed it fired **exactly 666 times with zero failure messages every time**, identically whether 100 items or only 2–3 items were checked — proving the loop was unrelated to what was being renamed. Removing the custom preprocessor entirely (commenting it out, then deleting it) fixed the rollback completely; live-confirmed the rename commits normally with no preprocessor at all. Root Revit-engine mechanism still unconfirmed, but merely *attaching* any custom `IFailuresPreprocessor` to this specific transaction is what breaks it — **do not re-add one here**, even the "safe" delete-warnings/resolve-errors pattern that works fine in `SplitWallCommand`. Since this transaction only renames elements (no geometry edits), unresolved warnings from unrelated pre-existing issues are rare in practice, so losing the suppression isn't a major loss. `Transaction.Commit()`'s `TransactionStatus` return value is still checked and surfaced via a `TaskDialog` if ever non-`Committed`, so a future rollback (whatever the cause) won't fail silently again.
- Renames happen in one `Transaction`, per-item `try/catch` (duplicate names, locked elements, etc. are common failure causes) with a summary `TaskDialog` listing failures — the same "collect skip reasons, one summary dialog" pattern as `SplitWallCommand`.
- **Two-stage apply** (added 2026-07-10, on request — chaining several operations without reopening NAMER each time was too tedious): "적용" only mutates two in-memory dictionaries, `_trueOriginalNames` (name when NAMER first saw that element this session) and `_workingNames` (accumulated result of every "적용" click so far) — it never touches the Revit model and the window stays open, so the user can switch category/mode and keep layering more operations on top of the same working names. "최종 적용" is the only button that sets `Result` and closes the dialog — it diffs `_workingNames` against `_trueOriginalNames` across *all* categories touched this session, not just the currently-visible one. "취소" discards everything for free, since nothing was ever written to Revit until this point — the whole in-window editing session is implicitly undoable by just not clicking "최종 적용". **Both dictionaries are seeded lazily, only for elements actually renamed** (`ApplyButton_Click` writes `_trueOriginalNames[el.Id]` the first time that specific element's name changes, never upfront) — an earlier version seeded every element in the *entire* loaded category (checked or not) as soon as `LoadCategory` ran, which meant opening "유형" alone did a `Name` read across every `ElementType` in the document before the user touched anything; flagged live as a real contributor to NAMER feeling sluggish and removed. `WorkingNameOf` already falls back to a live `el.Name` read when there's no dictionary entry, so nothing else needed to change for elements that stay untouched all session.
- **Unchecked items never get a *new* computed change, but a change already committed via "적용" survives being unchecked afterward** — this is a reversal (2026-07-13) of the original hard rule ("always gate the final result on current `_checkedIds`"). `ApplyButton_Click` still only computes `ComputeNewName` for currently-checked elements (so an item that was never checked can never enter `_workingNames` — that half of the original rule is untouched). But `FinalApplyButton_Click`/`UpdatePendingChangesText` **no longer** filter `_workingNames` by `_checkedIds.Contains(...)` — they just diff `_workingNames` against `_trueOriginalNames` directly. Root cause: `RenderRows()` auto-unchecks items that scroll out of the current filter (see "Filtering out an item also unchecks it" above), and with the old checked-gate this **silently and permanently discarded already-applied changes** the moment the user typed a different filter — live-reported as "필터에서 사라지면서 최종적용을 할 수 없게 됨". Since a change can only ever enter `_workingNames` through a deliberate checked "적용" click, letting it survive a later filter-driven auto-uncheck is safe and matches user intent; only an actually-untouched (never-applied) item is still excluded. `UpdateRowPreview` is unaffected by this change — unchecked rows still show their already-applied working name in gray without recomputing anything.
- **`ApplyButton_Click` clears every mode's input textboxes** (`ClearModeInputs`) right after a successful in-window apply — found live: leftover values (e.g. a still-filled 구간 삭제 start/end) would otherwise sit in the boxes and get re-applied on a second stray "적용" click, silently deleting another range from the just-shortened name. Safe to blanket-clear because every mode already treats empty/unparseable input as a no-op in `ComputeNewName`. `FinalApplyButton_Click` structurally never calls `ComputeNewName` at all — it only ever moves already-`_workingNames`-accumulated strings into `Result` — and neither apply button carries `IsDefault` (removed on request, "혹시 모르니") specifically so a stray Enter keypress while typing in a param box can't trigger either apply path unintentionally.
- Gotcha: bare `Visibility.Visible` in a `Window` code-behind is `CS0176`, not the enum — `FrameworkElement.Visibility` (an inherited instance property) shadows it, so it must be `System.Windows.Visibility.Visible`. Same collision class as `Grid`/`Control`/`Color` above — always fully-qualify.
- **기존 이름 더블클릭 → 그 항목 하나만 인라인 수정** (2026-09-21, 요청: *"검색해서 나온 기존이름 부분을 더블클릭시에 그 이름하나를 단일로 수정이 가능했으면 좋겠고"*): 행의 기존 이름 칸(`RenameRow.OldNameHost`)에서 `e.ClickCount == 2`일 때만 `BeginInlineEdit`으로 그 자리에 `TextBox`를 올린다(이름 `TextBlock`과 '특성' 버튼은 `Collapsed`). Enter/포커스 이탈이면 확정, Esc면 취소. 확정은 `ApplyManualName`이 처리하는데 **`ApplyButton_Click`과 똑같은 규칙으로 `_trueOriginalNames`를 "이 요소가 세션 중 처음 바뀌는 순간"에만 기록**한다 — 그래야 수동 수정과 일괄 작업을 섞어 써도 "진짜 원래 이름"이 한 번만 잡히고, 최종 적용의 diff가 어긋나지 않는다. 작업 모드(치환/삽입/…)를 전혀 거치지 않는 직접 편집이므로 **체크 여부와 무관하게** 동작한다(체크 게이트는 `ComputeNewName`을 돌리는 경로에만 있는 규칙이고, 이건 사용자가 그 항목을 콕 집어 고친 것이다). `ApplyManualName`은 목록을 **다시 그리지 않는다** — 행 하나의 `OriginalName`/`OldNameText.Text`만 갱신하므로 펼쳐 둔 페이지와 스크롤 위치가 그대로 남는다.
  - 더블클릭의 **첫 번째 클릭은 이미 `RowPanel_MouseLeftButtonDown`을 타고 체크 상태를 한 번 뒤집어 놓는다**(두 번째 클릭만 `e.Handled = true`로 막힌다). 그래서 `OldNameHost_MouseLeftButtonDown`은 편집을 열기 전에 `EndDrag()` 후 체크를 한 번 되돌린다 — 이름을 고치려고 더블클릭했을 뿐인데 체크가 바뀌어 있으면 그대로 일괄 작업에 휩쓸린다.
  - 기존 이름 칸이 `TextBlock` 하나에서 `Grid`(`OldNameHost`)로 바뀌었다. **열 너비 동기화(`NamerWindow_Loaded`/`NameColumnSplitter_DragDelta`)는 이제 `row.OldNameText.Width`가 아니라 `row.OldNameHost.Width`에 써야 한다** — `TextBlock`에 그대로 쓰면 호스트가 0폭이라 이름이 통째로 안 보인다.
  - `Autodesk.Revit.DB.Grid`(데이텀 그리드)와 이름이 겹치므로 `NamerWindow.xaml.cs`는 `using WpfGrid = System.Windows.Controls.Grid;`를 쓴다(`Visibility`/`Control`/`Color`/`Binding`과 같은 충돌 계열).
- **이름 위에 마우스를 올리면 오른쪽 끝에 '특성' 버튼** (같은 요청의 뒷부분): `PropsButton`은 이름 `TextBlock` **위에 겹쳐** 놓고 평소 `Visibility.Hidden`, 행의 `MouseEnter`/`MouseLeave`에서만 드러낸다. `Collapsed`가 아니라 `Hidden`인 이유, 그리고 자리를 따로 비워 두지 않고 겹쳐 놓는 이유는 같다 — 자리를 미리 차지하면 가뜩이나 좁은 이름 칸이 항상 34dip 줄어든다. 버튼 클릭이 행의 체크 토글에 먹히지 않는 것은 `ButtonBase`가 `MouseLeftButtonDown`을 Handled로 표시하고 `rowPanel`의 핸들러는 평범한 `+=`(handledEventsToo 아님)이기 때문이다 — **행 핸들러를 `AddHandler(..., true)`로 바꾸면 이 버튼이 눌릴 때마다 체크가 같이 토글된다.**
- **`NamerPropertiesWindow` — 왜 Revit 기본 창을 못 쓰는가** (2026-09-21, 요청: *"…유형편집 OR 특성창과 같이 선택한 이름을 지닌 것의 상세정보를 열람하고 수정할 수 있는 창을 열어줬으면… 가능하면 레빗에서 제공해주는 창 자체를 열어주는 방향으로"*): 실제로 되는지 5개 연도 참조 어셈블리로 실측한 뒤 **직접 만드는 쪽으로 결정**했다. 근거 두 가지를 그대로 남긴다 — 이 판단을 뒤집으려면 이 둘이 먼저 달라져야 한다.
  1. Revit 기본 대화상자를 여는 유일한 공개 수단은 `UIApplication.PostCommand(RevitCommandId)`이고, 이름 그대로 **지금 실행 중인 명령이 끝난 뒤(Idle)** 실행된다. `NamerWindow`는 `ShowDialog()`로 `NamerCommand.Execute` 안에서 모달로 떠 있으므로 창이 떠 있는 동안에는 절대 실행되지 않는다. 즉 기본 창을 열려면 NAMER를 먼저 닫아야 하고, 그러면 쌓아 둔 작업 중 이름이 전부 사라진다.
  2. 설령 닫더라도 **쓸 만한 `PostableCommand`가 없다.** 2023~2027 교집합을 전부 뽑아 보면 이 용도에 근접한 건 `TypeProperties` / `Materials` / `ManageViewTemplates` 정도인데, `TypeProperties`는 "현재 **선택된 부재**"의 유형을 여는 명령이라 유형·뷰·시트처럼 `Selection`에 넣을 수 없는 요소에는 쓸 수 없고, `Materials`/`ManageViewTemplates`는 특정 항목이 아니라 그냥 브라우저를 여는 명령이라 "선택한 이름을 지닌 것의 상세 정보"라는 요청 자체를 만족시키지 못한다.
- **`NamerPropertiesWindow`의 내용과 반영 시점**: Revit 유형 특성 대화상자/특성 팔레트가 보여 주는 것과 같은 것, 즉 **파라미터 목록**이다. `Element.GetOrderedParameters()`(특성 팔레트와 같은 순서)로 모으고 `Definition.GetGroupTypeId()` + `LabelUtils.GetLabelForGroup`으로 묶는다 — **2024부터 `BuiltInParameterGroup`이 `ForgeTypeId` 기반으로 바뀌었으므로 5개 연도에 모두 있는 이 조합만 쓸 것**(`ParameterGroup` 프로퍼티로 되돌리지 말 것). 반영 시점이 둘로 나뉘는 게 이 창의 핵심 설계다:
  - **이름**은 모델에 바로 쓰지 않고 `NewName`으로 돌려주면 `NamerWindow.ApplyManualName`이 작업 중 이름에만 반영한다. NAMER의 2단계 적용 구조를 깨지 않기 위해서다.
  - **그 밖의 특성**은 '확인'을 누르는 즉시 자체 `Transaction("NAMER 특성 편집")`으로 쓴다. Revit 특성 팔레트와 같은 동작이고, 값 종류가 제각각이라 NAMER처럼 미리보기로 쌓아 둘 수가 없다. 따라서 **"NAMER에서 취소하면 아무것도 안 쓴 것"이라는 성질은 이 창을 쓴 경우에는 성립하지 않는다**(특성은 이미 커밋됨). 창 아래에 그 사실을 적어 두었다.
  - 모달 WPF 창 안에서 `Transaction`을 여는 것은 이 프로젝트에서 이미 쓰는 방식이다(`RoomSeparatorWindow`/`RoomBoundingWindow`) — `ShowDialog()`가 `Execute` 안에서 동기적으로 막고 있으므로 API 컨텍스트가 그대로 유효하다.
  - **여기에도 커스텀 `IFailuresPreprocessor`를 붙이지 말 것** — `NamerCommand`의 666회 재처리 롤백 이력과 같은 이유다. 요소 하나가 `Set`을 거부해도 예외를 흘리지 않고 모았다가 마지막에 한 번에 알리며(흘리면 트랜잭션 전체가 취소된다), `Commit()`의 `TransactionStatus` 반환값도 반드시 확인한다.
- **파라미터 편집 규칙** (`CreateEditor`/`ApplyRow`): 실수(`Double`)와 정수는 **단위가 붙은 표시 문자열**(`AsValueString`)을 보여 주고 저장도 같은 형식을 해석하는 `SetValueString`으로 한다 — 내부 단위(피트) 원시값을 그대로 보여 주면 사용자가 "3000"을 입력했을 때 3000피트가 되는 사고가 난다. 정수는 열거형(기능/역할 등)이면 라벨 문자열로 들어오므로 `SetValueString`을 먼저 시도하고, 실패하면 숫자로 파싱해 `Set(int)`한다. Yes/No는 `Definition.GetDataType().TypeId == SpecTypeId.Boolean.YesNo.TypeId`로 판별해 체크박스로 낸다(`ForgeTypeId`는 `TypeId` 문자열로 비교). `ElementId` 파라미터는 **읽기 전용으로 표시만** 한다 — 제대로 편집하려면 대상 요소를 고르는 선택기가 필요해서 범위 밖이고, 가리키는 요소의 이름을 보여 주는 것만으로도 확인 용도는 충분하다.
  - **값이 요소 이름과 똑같은 문자열 파라미터는 잠근다**(`nameMirror`). 뷰 이름/유형 이름/시트 이름처럼 사실상 "이 요소의 이름"인 파라미터라, 위쪽 '이름' 칸과 두 군데서 따로 고치면 서로 덮어쓴다. `BuiltInParameter`로 식별하지 않고 값 비교로 판별하는 것은 의도적이다 — 언어/버전에 무관하고, 우연히 이름과 같은 값을 가진 다른 문자열 파라미터가 하나 잠기는 정도는 안내 문구로 충분히 설명된다.
  - 재료의 '클래스'는 파라미터가 아니라 `Material.MaterialClass` 속성이라 목록에 아예 안 나온다 — 재료일 때만 보이는 별도 칸으로 따로 다룬다(`재료 지정` 도구와 같은 이유).
- **'적용' 후에도 펼쳐 둔 분량과 스크롤 위치를 유지한다** (2026-09-21, 요청: *"스크롤을 실컷 내린상태에서 뭔가 변경하고 적용을 하게되면 다시 가장 윗부분으로 올라가져서…"* → 이어서 *"스크롤이 올라간다기보다는 더보기를 다시눌러서 내려가야하는 불편함"*): `RenderRows(preserveView: true)`가 다시 그리기 **전에** `_renderedCount`와 `ItemsScroll.VerticalOffset`을 붙잡아 두었다가, 첫 페이지를 그린 뒤 그만큼 `RenderMoreRows()`를 반복하고 오프셋을 되돌린다. 예전에는 `ApplyButton_Click`이 평범한 `RenderRows()`를 불러 `_renderedCount`가 0으로 돌아갔고, 수천 개짜리 "유형"에서 한참 펼쳐 놓고 작업하던 사용자가 **적용할 때마다 '더 보기'를 처음부터 다시 눌러 내려가야 했다.** 카테고리/필터가 바뀔 때는 목록 자체가 달라지므로 복원하지 않는 게 맞다(그래서 기본값은 `false`). 스크롤 복원은 `UpdateLayout()`으로 `ExtentHeight`를 확정한 뒤 `ScrollToVerticalOffset`을 부르고, `ScrollViewer`가 배치 직후 오프셋을 잘라내는 경우가 있어 `DispatcherPriority.Loaded`에서 한 번 더 같은 값을 쓴다.
- **검증 하네스** (`scratchpad/NamerPreview`): `NamerWindow`는 Revit `Document` 없이는 생성조차 안 되므로(`CollectCandidates`가 바로 문서를 훑는다), 행 구성/인라인 편집/페이지 복원 코드를 **그대로 옮긴** `RowsWindow`를 만들어 `SetCursorPos`+`mouse_event` 진짜 마우스 입력으로 확인한다(커스텀 버튼 드래그에서 라우팅 문제를 두 번 놓친 뒤 세운 방침). 확인 항목: 호버 시에만 버튼이 보이는지, 한 번 클릭은 체크만 바꾸는지, 더블클릭이 편집을 열고 **체크를 원상복구**하는지, 편집칸이 포커스를 받고 전체 선택되는지, Enter 확정/Esc 취소, '특성' 버튼 클릭이 행 핸들러를 아예 안 부르는지, 그리고 '더 보기' 두 번(600개) + 스크롤 뒤 `RenderRows(preserveView: true)`가 600개와 오프셋을 유지하는지(**`preserveView: false` 대조군이 실제로 200개로 되돌아가는 것까지 확인해 테스트가 헛돌지 않게 했다**). 특성 창은 XAML을 그대로 복제해 가짜 파라미터로 채운 뒤 `RenderTargetBitmap`으로 렌더해 레이아웃만 눈으로 확인했다.
  - 하네스에서 겪은 함정: 진짜 마우스 입력은 Win32 메시지 큐를 거쳐 들어오므로 **호출 직후에 바로 단언하면 타이밍에 따라 결과가 달라진다**(확인 한 줄을 넣고 빼는 것만으로 통과/실패가 뒤집혔다). 지금은 `Settle(ms)`로 계속 펌프하고 단언도 `Eventually`로 최대 1초까지 기다린다. 또 커서를 한 번에 목표 지점으로 옮기고 곧바로 누르면 `WM_MOUSEMOVE`가 처리되기 전에 누름이 먼저 들어와 엉뚱한 요소에 붙으므로, 옆으로 한 번 들렀다가 목표로 간다.

## 2026-09-21: 모드리스 전환 + '특성' 버튼이 Revit 기본 창을 연다

**이 문서 맨 위의 "NamerWindow is modal (ShowDialog())" 설명은 이제 옛날 이야기다.** 같은 날 사용자
요청으로 뒤집혔다: *"오히려 새로 만든게 더 어렵고 보기가 힘들어. 지금 NAMER가 모달창이라면, 네이머를
띄워두어도 다른작업이 병행가능한 창으로 변경해서라도 특성버튼을 누르면 레빗 자체의 유형편집이나
특성창을 띄울 수 있도록 했으면 좋겠어."*

- **왜 모드리스여야만 했는가**: Revit 기본 대화상자를 여는 유일한 공개 수단인 `UIApplication.PostCommand`는
  이름 그대로 **"지금 실행 중인 명령이 끝난 뒤(Idle)"** 에 실행된다. 모달 창은 `IExternalCommand.Execute`
  안에서 블록하고 있으므로 그 Idle이 오지 않고, 기본 창은 **영영 열리지 않는다**. 즉 "모달을 유지하면서
  기본 창을 연다"는 선택지가 애초에 없다 — 모드리스 전환은 취향이 아니라 이 기능의 전제조건이다.
  (문서 맨 위의 "모드리스로 '업그레이드'하지 말 것"이라는 옛 규칙이 요구한 "진짜 필요"가 바로 이것이다.)
- **구조**: `NamerCommand.Execute`는 창만 띄우고 즉시 반환한다(`NamerWindow.Instance`로 단일 인스턴스
  유지, 이미 열려 있으면 `UpdateDocumentAndSelection`으로 재사용 — 경고Pick과 같은 방침). 모델을 건드리는
  일은 전부 `NamerExternalEventHandler`를 거친다. 예전에 `Execute` 안에 있던 이름 변경 루프는
  `NamerCommand.ApplyRenames`로 그대로 옮겼을 뿐 내용은 바뀌지 않았다(**커스텀 `IFailuresPreprocessor`를
  붙이지 않는다는 규칙도 그대로 — 666회 재처리 롤백 이력 참고**).
- **모드리스가 되면서 반드시 달라져야 했던 것들**:
  - `DialogResult`를 쓰면 안 된다(모드리스 창에서 설정하면 예외). "취소" 버튼은 **"닫기"** 가 되었고
    `IsCancel`도 뗐다 — Esc 한 번에 쌓아 둔 작업 중 이름이 통째로 날아가면 안 되기 때문이다.
  - "최종 적용"이 더 이상 창을 닫지 않는다. 요청만 넣고, 커밋이 끝나면 핸들러가 `OnRenamesApplied`로
    결과를 돌려준다. 거기서 `_trueOriginalNames`/`_workingNames`를 **비우고** 목록을 다시 읽는다 —
    모델이 이미 새 이름을 갖고 있으므로 안 비우면 "아직 반영 안 된 변경"으로 계속 세어져 사용자가 또
    최종 적용을 누르게 된다. 체크 상태는 유지한다.
  - 결과를 대화상자로 막고 알릴 수 없으므로 창 아래 **상태 줄**(`StatusText`)을 추가했다.
  - **문서가 닫히면 창도 닫는다**(`Application.DocumentClosing`). `_categoryElements`가 들고 있는
    `Element`는 그 순간 전부 무효가 되어, 필터에 한 글자만 쳐도 `el.Name`에서 예외가 나고 그게 WPF
    이벤트 핸들러에서 새어 나가면 곧바로 Revit 충돌 대화상자가 된다. 안전망으로 `SafeNameOf`
    (`IsValidObject` 확인 + try/catch)도 함께 둔다.
  - 다른 문서로 갈아탄 채 다시 실행하면 체크/작업 중 이름을 **전부 비운다** — `ElementId`는 문서마다
    독립적이라 그대로 두면 최종 적용이 남의 요소를 바꾼다. 핸들러도 매 요청마다 `DocKey`(경로, 없으면
    제목)로 대상 문서를 확인한다(`Document`를 `ReferenceEquals`로 비교하지 말 것 — 경고Pick에서 확인된
    라이브 버그).
- **'특성' 버튼이 카테고리마다 어디로 가는가** (`NamerNativeProperties`). 2023~2027 `PostableCommand`
  교집합을 전부 뽑아 확인한 결과가 아래 표이며, **정확히 그 항목을 열 수 있는 것과 없는 것이 갈린다**:

  | 카테고리 | 방법 | 그 항목을 콕 집어 여는가 |
  |---|---|---|
  | 뷰/시트/범례/일람표 | 그 뷰를 활성화하고 **선택을 비운다** → 특성 팔레트가 그 뷰의 특성을 보여준다 | O |
  | 유형 | 그 유형을 쓰는 부재 하나를 선택하고 `PostableCommand.TypeProperties` | O |
  | 패밀리 | 그 패밀리의 유형 중 배치된 것 하나로 위와 동일 | O |
  | 뷰 템플릿 | `PostableCommand.ManageViewTemplates` | X (목록에서 직접 고름) |
  | 재료 | `PostableCommand.Materials` (재료 탐색기) | X (목록에서 직접 고름) |

  - 뷰를 열 때 **선택을 반드시 비우는 것이 중요하다** — 뭔가 선택돼 있으면 특성 팔레트가 그 선택 요소의
    특성을 보여줘서, 정작 열려던 뷰의 특성이 안 보인다.
  - 유형/패밀리는 **그 유형을 쓰는 부재가 모델에 하나도 없으면 열 수 없다** — Revit의 유형 특성 명령이
    "선택된 부재"를 기준으로 동작하는데 유형 자체는 `Selection`에 넣을 수 없기 때문이다. 이때만
    `NamerPropertiesWindow`(자체 창)로 넘어가며, **그 창은 ExternalEvent 안에서 띄운다** — 그 안은 유효한
    API 컨텍스트라 자체 `Transaction`이 그대로 동작한다(모드리스 창에서 직접 띄우면 트랜잭션을 못 연다).
  - 부재를 찾을 때는 **활성 뷰에 보이는 것을 먼저** 찾는다(화면에 없는 요소를 선택해 두면 창을 닫은 뒤
    "뭐가 선택된 거지?"가 된다). 로드된 패밀리는 `FamilyInstanceFilter`로, 나머지는 유형의 카테고리로 먼저
    좁힌 뒤 훑는다 — 카테고리로 안 좁히면 큰 모델에서 문서 전체 부재를 훑게 된다.
  - **이 버튼을 누르면 Revit의 현재 선택과 활성 뷰가 바뀐다.** 기본 창을 여는 대가이며 상태 줄에 그렇게
    적어 둔다. 되돌릴 방법은 없다(명령이 선택을 읽는 시점이 우리 코드가 끝난 뒤다).
  - `PlanFor(category)`는 **Revit 타입을 전혀 쓰지 않는 순수 함수로 떼어 놓았다** — 하네스에서 리플렉션으로
    8개 카테고리를 전부 돌려 표를 고정한다. 한 칸만 잘못 이어져도(예: 뷰 템플릿이 `ActivateView`로 가면
    "이 항목은 뷰가 아닙니다"로 끝난다) 라이브에서야 드러나는 종류의 실수다. **카테고리를 새로 추가하면
    이 표와 그 테스트도 같이 늘릴 것** (테스트가 카테고리 개수까지 확인한다).
- **'특성' 버튼은 `PrimaryButtonStyle`(강조색 배경 + 흰 글자)을 쓴다** (사용자 지적: *"특성버튼에 색을
  넣어주고, 특성버튼이 이름보다 항상 위를 덮고 있어서 버튼이 이름에 묻히지 않도록"*). 이 버튼은 이름
  글자 **위에 겹쳐** 놓이므로(자리를 미리 비우면 좁은 이름 칸이 항상 34dip 줄어든다) 배경이 불투명해야
  한다 — 기본 버튼 배경은 연해서 뒤의 이름이 비쳐 보였다. `Grid`는 나중에 추가한 자식이 위에 그려지므로
  이름 `TextBlock` **뒤에** 추가해야 한다(순서를 바꾸면 다시 글자에 묻힌다).
- **편집 중 아무 데나 클릭하면 편집에서 빠져나온다** (사용자 요청: *"입력칸에서 벗어나오려면 엔터,
  빈공간클릭을 해야하는데, 다른 어느부분을 클릭해도 벗어나올 수 있도록"*). `LostKeyboardFocus`만으로는
  부족했다 — 목록의 행(`StackPanel`)이나 라벨(`TextBlock`)처럼 **키보드 포커스를 가져가지 않는 요소**를
  클릭하면 포커스가 편집칸에 그대로 남아 편집이 안 닫힌다. 창 전체의 터널링 이벤트
  (`PreviewMouseDown`, `handledEventsToo: true`)에서 편집칸 밖이면 확정한다. **이벤트를 `Handled`로 막지
  않는 것이 중요하다** — 클릭한 곳이 원래 하려던 일(체크 토글, 버튼 누름)도 그대로 일어나야 "아무 데나
  클릭하면 빠져나온다"가 자연스럽다. 창이 비활성화될 때(`Deactivated`)도 확정한다(모드리스라 Revit 쪽을
  클릭하는 일이 흔하다).
- **기존 이름 칸은 항상 편집칸 높이(`InlineEditHeight`)를 확보한다**(`oldNameHost.MinHeight`). 편집칸은
  이름 `TextBlock`보다 키가 커서, 높이를 미리 잡아 두지 않으면 편집을 열고 닫을 때마다 그 행이 늘었다
  줄고 **아래 행들이 전부 몇 픽셀씩 밀린다**. 그 상태에서 다른 행을 클릭하면 편집이 닫히며 레이아웃이
  되돌아가 **겨눈 행이 아니라 옆 행이 눌린다** — 하네스의 행 위치 검증에서 실제로 잡힌 문제다.
- **하네스**(`scratchpad/NamerPreview`)도 위 변경을 그대로 반영해 두었고, 다음을 진짜 마우스 입력으로
  확인한다: 아무 데나 클릭해 편집에서 빠져나오는지(+ 이 처리를 끄면 실제로 안 닫히는 **대조군**),
  편집을 열고 닫아도 아래 행의 화면 위치가 그대로인지, 나머지 기존 항목 전부. 합성 입력의 함정 두 가지를
  새로 기록한다 — ① 같은 자리를 연달아 누르면 Windows가 **더블클릭으로 묶어 버려** 단일 클릭 테스트가
  깨진다(시스템 더블클릭 시간을 넘겨 떼어 놓을 것), ② 반대로 더블클릭의 네 입력 사이에서 디스패처를
  펌프하면 간격이 들쭉날쭉해져 **단일 클릭 두 번**이 되기도 한다(펌프하지 말고 연달아 넣을 것). 그래도
  배달이 어긋날 수 있어 `LastHostClickCount`로 **배달 여부만** 확인해 최대 세 번 다시 친다(검증 대상이
  아니라 입력 도착만 보므로 테스트가 무의미해지지 않으며, 실패한 시도가 뒤집어 놓은 체크는 되돌린다).
- **라이브 확인이 필요한 부분(하네스로는 확인 불가)**: `PostCommand(TypeProperties)`가 우리가
  프로그램적으로 넣은 선택을 기준으로 실제로 유형 특성 창을 여는지, `uidoc.ActiveView = view`가
  ExternalEvent 안에서 모든 뷰 종류에 대해 동작하는지. 둘 다 Revit 없이는 검증할 수 없다.

### CONFIRMED FATAL CRASH (2026-09-21, v84 → v85에서 수정): UIApplication을 창에 저장하지 말 것

**증상**: NAMER를 열었다가 **그냥 닫기만 해도** Revit이 "복구 불가능한 오류가 발생했습니다"를 띄우고
종료됐다(최종 적용 여부와 무관 — 처음 제보는 "최종 적용 후"였지만 저널상 이름 변경 트랜잭션은
`Transaction Successful`로 정상 커밋됐고, 크래시는 항상 창을 닫는 순간이었다).

**근본 원인**: v84가 `ExternalCommandData.Application`(`UIApplication`)을 `_uiApp` 필드에 저장해 두고,
창의 `Closed` 핸들러에서 `_uiApp.Application.DocumentClosing -= ...`을 호출했다. **`UIApplication`은 그
명령(`IExternalCommand.Execute`)이 실행되는 동안에만 유효하다** — 창이 닫히는 시점은 그보다 한참 뒤라
여기서 관리 예외가 났다.

**왜 그게 "치명적 오류"까지 갔나** — 모달일 때는 `ShowDialog()`가 `Execute` 프레임 안에서 돌기 때문에
WPF 핸들러의 예외가 Revit의 명령 래퍼까지 올라가 "애드인 오류" 대화상자로 끝났다. **모드리스에는 우리
핸들러와 Revit 네이티브 메시지 루프 사이에 관리 프레임이 하나도 없어서**, 같은 예외가 그대로 네이티브로
넘어가 `ExceptionCode=0xe0434352`(CLR 예외) 치명적 오류가 된다. 모드리스로 바꾸면서 같이 들어온 위험이다.

**저널이 남긴 결정적 증거**(`%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2026\Journals\journal.*.txt`):
`API_SUCCESS { Registering DocumentClosing event by application WallSplitter }`가 **두 번**(하나는
`App.OnStartup`, 하나는 NAMER 창 생성자), 그런데 `Unregistering ... WallSplitter`는 **한 번도 없이**
바로 `Exception occurred / ExceptionCode=0xe0434352`가 찍혔다 — 해지가 예외로 끝났다는 뜻이다.
**앞으로도 Revit 크래시는 추측하지 말고 이 저널부터 볼 것**(Windows 이벤트 로그에는 안 남는다 — Revit이
자체 크래시 보호로 예외를 잡아 프로세스 미처리 예외로 올라가지 않기 때문이다).

**고친 방식** (되돌리지 말 것):
- `NamerWindow`는 **`UIApplication`을 필드로도, 생성자 인자로도 받지 않는다.** Revit이 필요한 일은 전부
  `ExternalEvent`가 **그때그때 넘겨주는** `UIApplication`으로 한다(그건 호출 시점에 유효하다).
- 문서가 닫힐 때 창을 닫는 일은 `App.OnStartup`이 `ControlledApplication`에 이미 등록해 둔
  `OnDocumentClosing`이 `NamerWindow.Instance?.CloseForDocument(e.Document)`를 불러서 한다.
  **문서 이벤트 구독은 반드시 `OnStartup`의 `ControlledApplication`에서만 한다** — 세션 내내 유효한
  객체로 구독/해지해야 하고, 창 수명에 맞춘 구독/해지는 바로 이 사고를 만든다.
- 모드리스 창에서 Revit을 건드리거나 창을 닫는 WPF 핸들러는 전부 `Guard(...)`로 감싼다
  (`NamerWindow_Loaded`, `CategoryRadio_Checked`, `FilterBox_TextChanged`,
  `FilterModeCombo_SelectionChanged`, `PropsButton_Click`, `ApplyButton_Click`,
  `FinalApplyButton_Click`, `CloseButton_Click`, `Window_PreviewMouseDown`, `NamerWindow_Deactivated`,
  `Closed`). 실패하면 상태 줄에 알리고 끝난다 — **절대 예외를 밖으로 내보내지 않는다.**
  `Application.Current.DispatcherUnhandledException`으로 한 방에 막는 방법은 일부러 쓰지 않았다:
  `Application.Current`는 Revit·다른 애드인과 공유하는 객체라(테마 병합 사고와 같은 이유) 거기서
  `Handled = true`를 하면 **남의 예외까지 삼킨다.**
- 재발 방지 테스트(`scratchpad/NamerProbe/CrashRegressionTests.cs`)가 빌드된 5개 연도 DLL을
  `MetadataLoadContext`로 읽어 ① `UIApplication` 타입 필드가 없는지 ② 생성자가 그걸 받지 않는지
  ③ 창이 직접 문서 이벤트를 구독하던 메서드가 없는지 ④ `CloseForDocument`/`App.OnDocumentClosing`이
  남아 있는지 확인한다. **크래시가 나던 v84 빌드에 대고 돌리면 5개 연도 전부 FAIL로 잡히는 것까지
  확인했다**(대조군) — 테스트가 헛돌지 않는다는 증거다.
