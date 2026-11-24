# 모델간 변경 반영 (Model Sync)

## 2026-10-13 v100 — 프로젝트 정보/일람표 값 기록

- `ChangeKind.ProjectInformation`, `ScheduleParameter`를 뒤에 추가. 해당 편집기의 성공 후 기존 로그에 기록하되 다른 모델에 이름 기반으로 자동 재생하지 않는다. ResolveOne에서 명시적 제외 사유를 표시하고 ModelSyncWindow에도 한국어 종류를 표시한다.
- 프로젝트별 매개변수 ID/행 구성은 다른 문서에 이식할 수 없으므로 기존 Rename/MaterialAssign로 기록하지 말 것. 링크 작업은 중앙 동기화 성공 후에만 로그 확정하는 v99 경로 그대로다.

## 2026-10-13 v97 — 통합 편집의 모델별 기록

- 통합 NAMER/재료 지정은 각 모델의 스냅샷 변경을 UniqueId로 그 원본에 반영하는 기능이며 이름 매칭 기반 ModelSync 재생과 다르다. 기존 재생 구현은 변경하지 않는다.
- 링크 로그는 모델별 중앙 동기화 성공 뒤, 현재 모델 로그는 마지막 TransactionGroup 확정 뒤 추가한다. 중간 실패 시 성공한 앞 모델의 로그는 유지하고 미처리 모델의 로그는 만들지 않는다. 서버 오류로 중앙 반영이 불확실하면 성공 로그를 추측하지 않는다.

## 2026-10-13 v96 — 링크 중앙모델 편집 기록

- NAMER/재료 지정의 링크 원본 편집은 기존 모델간 변경 반영과 별개다. 중앙의 새 로컬에서 수집한 ChangeLogEntry를 deferredLogs로 보류하고, SynchronizeWithCentral 성공 뒤에만 Append한다. SourceDocumentTitle은 임시 로컬 폴더가 아닌 중앙 파일의 이름이다.
- 취소·적용 실패·동기화 완료 불확실 상태에서는 성공 로그를 추가하지 않는다. 현재 프로젝트의 기존 Append 경로는 그대로다. v94 인스턴스 재료 자동 재생 제외 규칙도 유지한다.
- ChangeLog 및 ModelSync 명령의 구현은 변경하지 않았다. 링크 편집 수명/실패 복구 제약은 namer v96 절을 함께 읽을 것.

## 2026-10-06 v94 — 인스턴스 재료 변경 기록의 안전한 분리

- 새 `ChangeKind.MaterialInstanceAssign`를 enum 끝에 추가한다. 같은 유형의 인스턴스 재료 일괄 변경은 기존 MaterialAssign(유형 슬롯 변경)과 다르므로 같은 종류로 기록하지 않는다.
- 기록 창에는 `인스턴스 재료 지정 (자동 반영 제외)`와 슬롯명을 표시한다. ResolveOne은 대상 요소 선택/트랜잭션 전 사유를 붙여 건너뛴다. 타 모델에 같은 이름의 유형이 있어도 해당 유형 재료를 덮어쓰지 않는다. 기존 기록 enum 값과 재생 경로는 유지한다.

`ModelSyncWindow.xaml(.cs)`/`ModelSyncCommand.cs`/`ChangeLog.cs`/`ChangeReplayEngine.cs`를 건드리기 전에 이 문서를 읽을 것. NAMER/재료 지정의 변경 기록을 재생하는 기능이라, 두 문서(`docs/namer`, `docs/material-assign`)와 함께 봐야 할 때가 많다.

Replicates NAMER/재료 지정 changes from one central model into another, added 2026-07-21 on request ("한 중앙모델에서 바뀐 걸 다른 중앙모델에도 같은 사례가 있으면 그대로 적용"). Own ribbon panel/button ("모델간 변경 반영"→`ModelSyncCommand`). Renamed from "모델 동기화" on 2026-07-24 (user feedback: the original name didn't intuitively convey what the feature does) — only user-facing strings/comments changed, `ModelSyncCommand`/`ModelSyncWindow`/`ModelSyncPanelName`/`ChangeReplayEngine` etc. keep their original identifiers (see `docs/design-system/CLAUDE.md`'s "화면 디자인 시스템" for the same conversation's other change).

- **`ChangeLog.cs`**: every successful NAMER rename (incl. materials, with the chosen 병합/접미사 policy) and every successful 재료 지정 assign/delete/class·설명 edit is auto-appended — right after each command's own `Transaction.Commit()` returns `Committed` (never for rolled-back or per-item-failed changes) — to `%APPDATA%\WallSplitter\change-log.json` (same global JSON pattern as `NamingSettings`). `ChangeLogEntry.Category`/`Field` deliberately reference `NamerWindow.NamerCategory`/`IdentityField` directly rather than duplicate enums, so a future 9th NAMER category can't silently drift out of sync with the replay side.
- **Matching = name only**, since `ElementId`s never carry across documents: Rename matches by the element's pre-change name within its NAMER category (`NamerWindow.CollectCandidates`, promoted to `internal` for this reuse); MaterialAssign matches by the *type's* name (`MaterialSlotFinder.FindEligibleTypes`, also promoted); MaterialDelete/MaterialIdentityEdit match by material name. Exactly one match auto-applies; 0 or 2+ opens `ElementPickerWindow` (a new small filter-box+list modal that only ever picks an existing element, never creates one) — **except MaterialDelete**, which just skips+reports on 0 matches instead of prompting "pick something else to delete instead" (a picker there would be a foot-gun toward deleting the wrong material).
- **`ChangeReplayEngine.RunForDocument`**: same 3-phase discipline as `SplitWallCommand` (resolve candidates → show every needed `ElementPickerWindow` → *then* open one `Transaction` per target document), since modal dialogs are never shown inside an open transaction anywhere in this codebase. Entries always apply in `Timestamp` order (not selection order) — a later entry can depend on an earlier one's result landing first (e.g. rename X→Y, then edit Y's class). Reuses `NamerCommand.RenameMaterial`/`MaterialAssignCommand.TryAssignMaterial`/`TryDeleteMaterial`/`TrySetMaterialIdentity` (all promoted to `internal`) instead of re-implementing apply/verify logic, and sets **no custom `IFailuresPreprocessor`** on its transaction, for the same confirmed-live-bug reason as `NamerCommand`/`MaterialAssignCommand`.
- **Two access paths, both supported** (explicit request): export/import a `.json` changeset file (for a target model opened in a separate Revit session later), or apply directly to any other already-open, non-linked, non-family `Document` in the *same* session (`ModelSyncWindow`'s target-document checklist, from `UIApplication.Application.Documents`) — both funnel into the same `ChangeReplayEngine`. Imported entries stay session-scoped to the window only, never merged into the local `change-log.json`, so a teammate's exported history never mixes into this machine's own auto-recorded log.
- **문서 비교와 기록 저장 보강 (2026-09-01, 전체 점검에서 선제 보강)**: `ModelSyncWindow.BuildTargetDocumentRows`가
  "현재 문서" 표시/기본 체크를 `ReferenceEquals(doc, _activeDoc)`로 판정하고 있었다. `Document`는 Revit API 래퍼
  객체라 같은 열린 문서라도 조회 시점마다 다른 인스턴스일 수 있다(경고Pick의 CONFIRMED LIVE BUG와 같은 원인,
  `docs/warning-pick/CLAUDE.md`). 경로(저장 안 된 문서는 제목) 값 비교를 하는 `IsSameDocument`로 바꿨다 —
  `Document`를 다시 참조 비교로 되돌리지 말 것.
  또 `ChangeLog.Append`의 `Save()`가 예외 처리 없이 호출되고 있었는데, 이 시점엔 모델 변경이 이미 커밋된
  뒤라 `%APPDATA%` 쓰기 실패만으로 커맨드 전체가 실패로 보고되면 실제로는 잘 반영된 작업이 실패한 것처럼
  보인다 — 기록 저장만 조용히 포기하게 했다. 기록 삭제 버튼(`ModelSyncWindow`)의 `log.Save()`도 감쌌다
  (WPF 이벤트 핸들러에서 예외가 새면 Revit 프로세스가 그대로 죽는다).
