# 일람표 에디터 (Schedule Editor)

## v101 — 2026-10-14 드래그 / Shift 범위 체크·해제

- `ScheduleEditorWindow`만 변경. NAMER 통합 창과 같은 시작 행 반대 상태 고정 드래그, 마지막 클릭 행 기준 Shift 범위 선택/해제. 빠르게 건너간 중간 행도 처리하고 되짚어도 반전하지 않는다. Shift 클릭 뒤 기준은 그 클릭 행으로 갱신한다.
- `_selected`가 선택 상태를 보관한다. 체크를 바꿀 때 행을 재생성하지 않고 체크/배경/상태만 갱신한다. `_syncingRange`로 압축 범위 문자열(`1-10,12`)의 역방향 TextChanged 재진입을 차단한다. 직접 범위 입력은 이전 Shift 기준과 드래그를 종료하며, 잘못된 입력이면 선택을 비우고 오류 표시; Stage는 기존 파서로 다시 검사한다. 필드 변경·더 보기·미리보기 적용에는 선택/페이지/스크롤을 유지한다.
- 행 전체가 입력 영역이며 체크박스는 포인터 히트 대상에서 제외해 이중 토글을 막는다. 체크박스의 키보드/접근성 Click은 같은 선택 경로를 사용한다. 목록이 마우스를 캡처하고 끝 24dip에서 22dip씩 스크롤한다(80ms 타이머, 정지한 커서도 이어서 스크롤). MouseUp/왼쪽 버튼 해제/목록 자체 LostMouseCapture/Deactivated/Closed/Render/직접 입력/예외에서 캡처·타이머를 정리한다. 자식의 LostMouseCapture 버블링으로 취소하지 말 것.
- 범위는 전체 데이터 행 번호 기준이고 200행 페이지를 넘어도 포함한다. 선택은 기존처럼 편집 불가 행도 가능하지만 Stage의 모호성·읽기 전용 차단은 바꾸지 않았다. Revit 행 대응/저장/동기화 서비스는 변경하지 않는다. 선택/해제는 누적 미리보기 값을 지우지 않는다.
- `.codex-build/unified-v101`: 기존 114 + 새 선택 회귀 31 = 145개. 새 검사는 드래그/Shift 양방향, 직접 입력, 잘못된 입력, 페이지 경계, 필드 전환, 실제 WPF 라우팅 이벤트·히트테스트·자동 스크롤·캡처/종료, 재생성 없는 체크 및 원본 불변을 포함한다. release-v101 기존 91 + link-safety-v101 72 = 총 308개. 기본/최소 창 렌더 확인, 2023~2027 컴파일 오류 0. OS 실제 마우스/키보드 입력과 Revit 실모델·중앙 동기화는 별도 라이브 확인 필요.

## v100 — 2026-10-13 첫 구현

사용자 요청: 별도 리본 버튼에서 특정 일람표의 필드와 정렬 기준 위에서부터의 행 번호를 지정해 값을 일괄 입력/변경. 링크 원본의 중앙 동기화까지 처리. 묶인 행은 **대상 요소를 확인할 수 있을 때 개수를 보여주고 전부 수정, 모호하면 차단**으로 명시 확정했다.

### 파일 / 흐름

- `ScheduleEditorCommand`: 일람표 선택, snapshot, 모달 순수값 UI, 최종 확인, 다시 조회해서 행 fingerprint 비교, 링크부터/현재 모델 마지막 처리. 이미 중앙 반영했거나 복구 변경을 남겼으면 이후 실패가 있어도 외부 명령은 Succeeded로 반환하고 실패를 별도 보고한다(v99 자동 롤백 회귀 방지).
- `ScheduleEditorService`: Revit 뷰 범위 collector로 현재 및 링크의 표시 요소 조회. 링크 collector는 2024+ 3인자 API. 2023 링크 포함 일람표는 명시적 차단. `UnifiedEditService.Collect(readRows:false)`로 모델 편집 가능 여부만 조회한다.
- `ScheduleEditorModel/Logic`: 순수 값, 길이 접두 키, 범위 `1-10,12` 파싱, 행-요소 집합 매칭, 전부 검증 후 미리보기 값 누적. 동일 문서/요소/매개변수는 공유 행 객체로 하나만 수정하며 여러 행에서 보이면 마지막 지정값이 같이 표시된다.
- `ScheduleEditorWindow`: 필드/행/값, 행별 소속 모델·실제 요소 수·원래 값·변경 미리보기·상태, 체크 및 범위 입력, 조절 가능한 열, 200행 페이징, 최종 적용. UI에서 Revit API 호출/문서 보관 없음. local Theme only.
- `ProjectInformationService` 재사용: 순수 매개변수 스냅샷, 저장형별 Set, 원시값 재검증, transaction 성공 후 로그. 이 파일을 수정할 때 NAMER 규칙도 반드시 읽을 것.

### 행을 추측하지 않는 제한

공개 API의 표 셀은 곧바로 요소 ID를 주지 않는다. 임시 복제 일람표에서 원래 정렬/필터/그룹 유지, 제목/열 제목/소계/합계/빈 구분행만 제외하고 데이터 행을 읽는다. 정렬의 숨은 필드는 복제에서만 표시한다. Count 필드를 읽어 실제 그룹 요소 개수까지 대조한다. 복제 트랜잭션은 성공/실패 모두 RollBack하며 원본 뷰와 요소 값은 변경하지 않는다. 실제 Revit에서 이 경로는 아직 미검증이므로 테스트 문서에서 먼저 검증해야 한다.

- 개별 행: 모든 후보가 직접 문자열 매개변수로 해석 가능한 표시 필드들의 값으로 정확히 1:1일 때만 매칭.
- 묶인 행: 모든 정렬/그룹 필드가 직접 문자열 매개변수여야 함. 표시 순서는 Revit이 만든 표를 그대로 사용하고 자체 정렬하지 않음. 텍스트 기준 그룹 키의 후보 집합, 데이터 행 키, Count가 모두 맞아야 함. 정렬 기준이 없으면 한 그룹/한 데이터 행인 경우만 허용.
- 동일 표시 키가 여러 행이면 순번으로 분배하지 말 것. 후보-행 집합 불일치/개수 불일치도 전체 조회 중단. 숫자 반올림/단위/레벨/계산식 기준을 텍스트로 임의 변환해 지원한다고 주장하지 말 것.
- 원본 요소·유형의 필터/정렬 매개변수 스냅샷과 유형 UniqueId를 보관. 실제 쓰기 직전 각 원본 문서에서 재검사. 조회 후 바뀐 값/유형/범위는 덮어쓰지 않음. 새 요소를 기존 선택 범위에 자동 추가하지 않음.
- 직접 Instance/ElementType 매개변수만 편집. 계산식/합성/관련 요소/합계 표시(DisplayType != Standard)/ElementId 참조값은 차단. 링크 사용자 매개변수는 공유 GUID로만 매칭(문서별 숫자 ID나 이름 추정 금지). 유형 값은 선택 밖 동일 유형에도 영향이 있어 UI/최종 확인에 안내.
- 일반 요소 일람표만. 키/재료 수량 산출/내포 일람표 제외. 후보 20,000개, 표 5,000행 상한. 필터 의존값을 직접 재검증할 수 없으면 중단.

### 중앙/로컬 안전

`LinkedModelEditService.Run`을 그대로 재사용. 운영 파일 직접 SaveAs/파일 복사 덮어쓰기 금지. 같은 Revit의 열린 원본/로컬을 재사용할 경우 기존 작업 포함 동기화 확인(기본 No), 문서/소유권 유지. 없으면 전용 로컬. 같은 파일의 여러 배치에서 나온 값은 한 번만 수정. 다른 사용자의 로컬을 원격으로 강제 저장/갱신하지 않는다. 현재 모델은 자동 동기화하지 않는다. 중앙 성공 뒤만 로그. 중간 실패는 나머지 파일 중단, 이미 동기화한 파일은 자동 되돌릴 수 없음. IFailuresPreprocessor 추가 금지.

### 검증

`.codex-build/unified-v100`에서 실제 모델/서비스/UI + Revit 대역을 실행. 행 범위, 충돌 방지 키, 표시 순서, 그룹 전체/개별 모호성 차단, 숨은 정렬/원본 보존/임시 복제 정리, Count 불일치, 유형 공동 편집, 입력 오류 원자성, 링크 ID 격리, 정렬 의존값 변경 감지 등 27개. 실제 코드는 2023~2027 빌드. Revit/서버는 [testing.md](testing.md) 별도 수동 검증.

공식 API 근거: [ViewSchedule / 그룹·필터·표시 요소](https://help.autodesk.com/cloudhelp/2023/ITA/Revit-API/files/Revit_API_Developers_Guide/Basic_Interaction_with_Revit_Elements/Views/View_Types/TableView/ViewSchedule/Revit_API_Revit_API_Developers_Guide_Basic_Interaction_with_Revit_Elements_Views_View_Types_TableView_ViewSchedule_Working_with_ViewSchedule_html.html), [ScheduleField](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/3d6b0eb5-ed36-278d-a5df-38b6d600e876.htm). 설치 API XML의 CustomField 인터페이스는 내부 API라고 명시되어 있어 사용하지 않았다.
