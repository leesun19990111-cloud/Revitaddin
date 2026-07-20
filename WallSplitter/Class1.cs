using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Autodesk.Revit.Attributes;

namespace WallSplitter
{
    [Transaction(TransactionMode.Manual)]
    public class SplitWallCommand : IExternalCommand
    {
        // 두께가 사실상 0인 레이어(멤브레인/방수층 등)는 벽 유형을 만들 수 없으므로 건너뛰기 위한 기준값 (내부 단위 ft)
        private const double MinLayerWidth = 1e-9;

        // 벽 하나를 분리하기 위해 미리 읽어 캐시해 둔 정보 (원본 벽 삭제 전에 수집).
        private class WallPrepData
        {
            public Wall Wall;
            public ElementId WallId;          // 원본 벽이 삭제된 뒤에도 참조하기 위한 캐시
            public Curve BaseCurve;
            public CompoundStructure Structure;
            public XYZ Orientation;
            public double TotalWidth;
            public ElementId LevelId;
            public bool HasEditedProfile;
            public CurveArrArray OriginalProfile;
            // 이 벽의 끝(0/1)에서 결합되어 있던 다른 요소들의 ElementId (원본 벽 삭제 전 상태)
            public HashSet<ElementId>[] ElementsAtJoin = new HashSet<ElementId>[2];
            // 두께 0(멤브레인) 레이어를 제외한, 실제로 단일 벽이 만들어질 레이어들 (오프셋 계산까지 미리 끝낸 상태).
            public List<LayerDescriptor> LayerDescriptors = new List<LayerDescriptor>();
            // NamingMode.DirectType일 때만 사용: LayerDescriptors와 같은 순서로, 사용자가 지정한 WallType Id.
            public List<ElementId> DirectTypeIds = new List<ElementId>();
        }

        // 원본 복합벽의 레이어 하나에서 실제로 단일 벽을 만드는 데 필요한 정보 (두께 0 레이어는 애초에 제외됨).
        // Template/DirectType 두 모드가 오프셋 계산 로직을 공유하도록 Tx1 루프보다 먼저 한 번만 계산해 둔다.
        private class LayerDescriptor
        {
            public double LayerWidth;   // ft
            public double CenterOffset; // ft, 원본 중심선 기준 부호 있는 오프셋
            public ElementId MaterialId;
            public string MaterialName = "";
            public MaterialFunctionAssignment Function;
        }

        // 1차 트랜잭션에서 스케치만 만들어 두고, 커밋 후 SketchEditScope로 처리할 프로파일 복사 작업.
        // Mapping: 원본 프로파일 곡선을 이 레이어의 새 벽 위치로 옮기는 변환.
        //   - 직선 벽: 중심선을 옮길 때 쓴 것과 정확히 같은 평행 이동 (Transform.CreateTranslation).
        //   - 호(Arc) 벽: 중심축(원래/오프셋 호가 공유하는 중심점) 기준 반경 방향 스케일
        //     (BuildArcProfileMapping 참고). 두 경우 모두 높이(Z)는 항상 그대로 보존된다.
        private class ProfileTask
        {
            public ElementId SketchId;
            public CurveArrArray OriginalProfile;
            public Transform Mapping;
            public string WallTypeName;
        }

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                // [선택 단계] 버튼을 누르기 전에 이미 벽을 선택해 뒀다면 그 선택을 그대로 사용하고,
                // 아무것도 선택하지 않은 상태라면 그때만 마우스로 직접 고르게 한다.
                Selection sel = uiDoc.Selection;
                List<Wall> candidateWalls = new List<Wall>();

                ICollection<ElementId> preSelectedIds = sel.GetElementIds();
                if (preSelectedIds.Count > 0)
                {
                    foreach (ElementId id in preSelectedIds)
                        if (doc.GetElement(id) is Wall preSelectedWall)
                            candidateWalls.Add(preSelectedWall);
                }
                else
                {
                    IList<Reference> pickedRefs = sel.PickObjects(
                        ObjectType.Element,
                        new WallSelectionFilter(),
                        "분리할 복합 벽을 선택하세요 (여러 개 선택 가능 - 서로 결합된 벽을 함께 선택하면 분리 후에도 같은 재료끼리 결합됩니다). 완료 후 Enter 또는 완료 버튼.");

                    if (pickedRefs.Count == 0)
                        return Result.Cancelled;

                    foreach (Reference pickedRef in pickedRefs)
                        if (doc.GetElement(pickedRef) is Wall pickedWall)
                            candidateWalls.Add(pickedWall);
                }

                List<WallPrepData> preps = new List<WallPrepData>();
                List<string> skipReasons = new List<string>();

                foreach (Wall wall in candidateWalls)
                {
                    // 직선(Line) 또는 호(Arc) 위치선을 가진 벽을 지원한다.
                    if (!(wall.Location is LocationCurve locCurve) || !(locCurve.Curve is Line || locCurve.Curve is Arc))
                    {
                        skipReasons.Add($"{wall.Id}: 직선/호 형태가 아니라 건너뜀");
                        continue;
                    }

                    WallType wallType = wall.WallType;
                    CompoundStructure structure = wallType.GetCompoundStructure();
                    if (structure == null)
                    {
                        skipReasons.Add($"{wall.Id}: 레이어 구조 정보가 없어 건너뜀");
                        continue;
                    }

                    WallPrepData prep = new WallPrepData
                    {
                        Wall = wall,
                        WallId = wall.Id,
                        BaseCurve = locCurve.Curve,
                        Structure = structure,
                        Orientation = wall.Orientation.Normalize(),
                        TotalWidth = structure.GetWidth(),
                        LevelId = wall.LevelId,
                    };

                    // 프로파일 편집(Edit Profile)으로 기본 직사각형이 아닌 형태를 가진 벽이라면,
                    // 원본 벽을 삭제하기 전에 스케치 프로파일 곡선을 미리 읽어 캐시해 둔다.
                    if (wall.SketchId != ElementId.InvalidElementId)
                    {
                        Sketch originalSketch = doc.GetElement(wall.SketchId) as Sketch;
                        prep.OriginalProfile = originalSketch?.Profile;
                        prep.HasEditedProfile = prep.OriginalProfile != null;
                    }

                    // 원본 벽이 양 끝에서 어떤 요소와 결합되어 있었는지 미리 기록 (분리 후 같은 재료끼리 재결합하기 위함)
                    for (int end = 0; end < 2; end++)
                    {
                        HashSet<ElementId> joined = new HashSet<ElementId>();
                        foreach (Element joinedElem in locCurve.get_ElementsAtJoin(end))
                        {
                            // ElementsAtJoin은 자기 자신을 포함해 반환할 수 있으므로 제외
                            if (joinedElem.Id != wall.Id)
                                joined.Add(joinedElem.Id);
                        }
                        prep.ElementsAtJoin[end] = joined;
                    }

                    preps.Add(prep);
                }

                if (preps.Count == 0)
                {
                    TaskDialog.Show("오류", "분리 가능한 복합 벽이 없습니다.\n" + string.Join("\n", skipReasons));
                    return Result.Cancelled;
                }

                // "설정" 버튼에서 저장해 둔 단일 벽 유형 이름 형식/지정 방식 (없으면 기본값)
                NamingSettings namingSettings = NamingSettings.Load();

                // 레이어 오프셋 계산은 Template/DirectType 두 모드가 공유하므로, Tx1보다 먼저(트랜잭션 밖에서,
                // 순수 읽기 전용으로) 모든 prep에 대해 한 번만 계산해 둔다. DirectType 모드의 유형 지정 창도
                // 이 결과(레이어별 재료/두께)를 그대로 보여준다.
                foreach (WallPrepData prep in preps)
                    prep.LayerDescriptors = BuildLayerDescriptors(doc, prep, skipReasons);

                // [유형 지정 단계] DirectType 모드면 Tx1을 열기 전에(모달 창은 열린 트랜잭션 안에서 띄우지 않는다,
                // NamerCommand와 같은 원칙) 원본 벽마다 레이어별 유형을 확정해 둔다. 사용자가 취소하면 전체 취소.
                if (namingSettings.Mode == NamingMode.DirectType)
                {
                    List<ElementType> availableTypes = new FilteredElementCollector(doc)
                        .OfClass(typeof(WallType))
                        .Cast<WallType>()
                        .Where(wt => wt.Kind == WallKind.Basic)
                        .Cast<ElementType>()
                        .OrderBy(t => t.Name)
                        .ToList();

                    foreach (WallPrepData prep in preps)
                    {
                        if (prep.LayerDescriptors.Count == 0) continue; // 전부 멤브레인이라 만들 벽이 없음

                        List<(ElementId MaterialId, double WidthFeet)> signature = prep.LayerDescriptors
                            .Select(d => (d.MaterialId, d.LayerWidth))
                            .ToList();

                        bool reusable = false;
                        List<ElementId> reusedIds = new List<ElementId>();
                        string mismatchReason = null;

                        if (namingSettings.TypeAssignmentPersistence == TypeAssignmentPersistence.Multiple)
                            reusable = TypeAssignmentSession.TryGetReusableSelection(TypeAssignmentTarget.Wall, doc, signature, out reusedIds, out mismatchReason);

                        if (reusable)
                        {
                            prep.DirectTypeIds = reusedIds;
                            continue;
                        }

                        // '복수' 모드인데 재사용에 실패한 경우에만 mismatchReason이 채워져 있다(=빨간 글씨로 표시할 사유).
                        // '단일' 모드이거나 이번 세션 첫 지정이면 null이라 경고 없이 안내 문구만 보여준다.
                        string warningMessage = namingSettings.TypeAssignmentPersistence == TypeAssignmentPersistence.Multiple
                            ? mismatchReason
                            : null;

                        List<LayerPickItem> items = prep.LayerDescriptors
                            .Select((d, i) => new LayerPickItem { Index = i, MaterialName = d.MaterialName, ThicknessMm = d.LayerWidth * 304.8 })
                            .ToList();

                        string header = $"'{prep.Wall.WallType.Name}' 벽 분리 - 레이어 {items.Count}개에 적용할 유형을 검색하여 지정하세요.";

                        LayerTypeAssignmentWindow pickerWindow = new LayerTypeAssignmentWindow(header, items, availableTypes, warningMessage);
                        new WindowInteropHelper(pickerWindow) { Owner = commandData.Application.MainWindowHandle };
                        if (pickerWindow.ShowDialog() != true)
                            return Result.Cancelled; // 취소 시 벽체분리 전체 작업을 취소 (확인된 요구사항)

                        List<ElementId> resolvedIds = pickerWindow.Result.Select(t => t.Id).ToList();
                        prep.DirectTypeIds = resolvedIds;

                        if (namingSettings.TypeAssignmentPersistence == TypeAssignmentPersistence.Multiple)
                            TypeAssignmentSession.Remember(TypeAssignmentTarget.Wall, doc, signature, resolvedIds);
                    }
                }

                List<string> profileWarnings = new List<string>();
                List<ProfileTask> profileTasks = new List<ProfileTask>();

                // (원본 벽 Id, 생성된 단일 벽의 WallType Id) -> 그 유형으로 만들어진 새 단일 벽들.
                // 한 벽 안에 같은 재료+두께 레이어가 두 번 나오는 경우(대칭 구조)를 위해 List로 보관한다.
                Dictionary<(ElementId, ElementId), List<Wall>> createdMap = new Dictionary<(ElementId, ElementId), List<Wall>>();

                // [1차 트랜잭션] 단일 벽 유형/벽체 생성 + 프로파일 스케치 준비 + 원본 복합 벽 삭제
                using (Transaction tx = new Transaction(doc, "단일 벽 유형 생성 및 벽체 분리"))
                {
                    tx.Start();

                    foreach (WallPrepData prep in preps)
                    {
                        for (int layerIndex = 0; layerIndex < prep.LayerDescriptors.Count; layerIndex++)
                        {
                            LayerDescriptor d = prep.LayerDescriptors[layerIndex];

                            WallType targetWallType;
                            string newTypeName;

                            if (namingSettings.Mode == NamingMode.DirectType)
                            {
                                // [유형 단계 - 직접 지정] 위에서 이미 확정해 둔, 문서에 있는 기존 유형을 그대로 쓴다
                                // (자동 생성 없음).
                                targetWallType = doc.GetElement(prep.DirectTypeIds[layerIndex]) as WallType;
                                newTypeName = targetWallType.Name;
                            }
                            else
                            {
                                // [유형 단계 - 이름 형식] 설정된 이름 형식으로 단일 벽 유형을 찾거나 생성.
                                // 같은 이름의 기존 유형이 있어도 재료/두께가 다르면 재사용하지 않고 뒤에 (2), (3)…을 붙인다.
                                newTypeName = namingSettings.BuildName(d.MaterialName, d.LayerWidth * 304.8);
                                targetWallType = GetOrCreateSingleLayerWallType(
                                    doc, prep.Wall.WallType, newTypeName, d.LayerWidth, d.Function, d.MaterialId);
                            }

                            // [생성 단계] 이 레이어의 중심선 오프셋 계산 후 실제 단일 벽체 생성
                            Curve offsetCurve = OffsetCurvePerpendicular(prep.BaseCurve, d.CenterOffset, prep.Orientation);

                            Wall newWall = Wall.Create(
                                doc,
                                offsetCurve,
                                targetWallType.Id,
                                prep.LevelId,
                                /*height*/ 10.0,   // 임시값. 아래에서 원본 벽의 상/하 구속으로 덮어씀
                                /*offset*/ 0,
                                /*flip*/ false,
                                /*structural*/ false);

                            // 원본 벽의 상/하 구속 조건을 새 벽에 복사
                            CopyParam(prep.Wall, newWall, BuiltInParameter.WALL_BASE_OFFSET);
                            CopyParam(prep.Wall, newWall, BuiltInParameter.WALL_HEIGHT_TYPE);
                            CopyParam(prep.Wall, newWall, BuiltInParameter.WALL_TOP_OFFSET);
                            CopyParam(prep.Wall, newWall, BuiltInParameter.WALL_USER_HEIGHT_PARAM);

                            // 기본값: 새 벽들끼리, 그리고 주변 벽과 자동 결합(Join)되지 않도록 방지.
                            // "선택된 다른 원본 벽과 같은 재료로 결합되어 있던 경우"에만 마지막 단계에서 다시 허용한다.
                            WallUtils.DisallowWallJoinAtEnd(newWall, 0);
                            WallUtils.DisallowWallJoinAtEnd(newWall, 1);

                            // 원본이 프로파일 편집된(비직사각형) 벽이었다면 여기서는 기본 프로파일 스케치만 만들어 두고,
                            // 실제 곡선 복사는 이 트랜잭션이 커밋된 뒤에 한다.
                            // (SketchEditScope는 열려 있는 트랜잭션 안에서는 시작할 수 없다는 API 제약 때문)
                            //
                            // 직선 벽: 중심선을 옮긴 것과 정확히 같은 벡터로 프로파일 곡선을 평행 이동.
                            // 호(Arc) 벽: 오프셋 전후 호가 중심점을 공유하는 동심원이라는 사실을 이용해
                            // 그 중심축 기준 반경 스케일(BuildArcProfileMapping)로 매핑. 둘 다 실패하면 경고만 남기고
                            // 새 벽은 기본 사각형 프로파일로 남는다.
                            if (prep.HasEditedProfile)
                            {
                                Transform mapping = prep.BaseCurve is Line
                                    ? Transform.CreateTranslation(prep.Orientation * d.CenterOffset)
                                    : BuildArcProfileMapping(prep.BaseCurve as Arc, offsetCurve as Arc);

                                if (mapping == null)
                                {
                                    profileWarnings.Add($"{newTypeName}: 곡선(호) 벽의 오프셋 형태를 분석하지 못해 기본 사각형 형태로 생성되었습니다.");
                                }
                                else if (newWall.CanHaveProfileSketch())
                                {
                                    Sketch defaultSketch = newWall.CreateProfileSketch();
                                    profileTasks.Add(new ProfileTask
                                    {
                                        SketchId = defaultSketch.Id,
                                        OriginalProfile = prep.OriginalProfile,
                                        Mapping = mapping,
                                        WallTypeName = newTypeName,
                                    });
                                }
                                else
                                {
                                    profileWarnings.Add($"{newTypeName}: 이 벽 유형은 프로파일 편집을 지원하지 않습니다.");
                                }
                            }

                            if (!createdMap.TryGetValue((prep.WallId, targetWallType.Id), out List<Wall> sameTypeWalls))
                            {
                                sameTypeWalls = new List<Wall>();
                                createdMap[(prep.WallId, targetWallType.Id)] = sameTypeWalls;
                            }
                            sameTypeWalls.Add(newWall);
                        }
                    }

                    // 원본 복합 벽체는 결합 허용 전에 삭제해서, 새 벽이 곧 사라질 원본과 결합되는 일을 방지
                    foreach (WallPrepData prep in preps)
                        doc.Delete(prep.WallId);

                    tx.Commit();
                }

                // [2차: 프로파일 복사] SketchEditScope는 자체적으로 트랜잭션을 관리하므로 1차 커밋 후 수행
                foreach (ProfileTask task in profileTasks)
                {
                    string warning = TryCopyProfileSketch(doc, task);
                    if (warning != null)
                        profileWarnings.Add($"{task.WallTypeName}: {warning}");
                }

                // [3차: 재결합] 선택된 원본 벽들끼리 결합되어 있던 끝에서, 같은 재료(WallType)로
                // 분리된 새 벽들의 위치선을 실제 코너 교차점까지 트림한 뒤 결합을 허용한다.
                // (중심선과 겹치는 레이어는 트림해도 좌표가 그대로라 기존과 동일하게 동작한다.)
                List<JoinPair> joinPairs = CollectJoinPairs(preps, createdMap);
                if (joinPairs.Count > 0)
                {
                    using (Transaction tx = new Transaction(doc, "같은 재료 벽 재결합"))
                    {
                        tx.Start();
                        foreach (JoinPair pair in joinPairs)
                        {
                            TrimToIntersection(pair);
                            WallUtils.AllowWallJoinAtEnd(pair.WallA, pair.EndA);
                            WallUtils.AllowWallJoinAtEnd(pair.WallB, pair.EndB);
                        }
                        tx.Commit();
                    }
                }

                // 정상 완료 시에는 팝업을 띄우지 않는다 (요청 사항).
                // 다만 일부 항목이 처리되지 못한 경우는 완료 알림이 아니라 경고이므로 알려준다.
                List<string> warnings = new List<string>(skipReasons);
                warnings.AddRange(profileWarnings);
                if (warnings.Count > 0)
                {
                    TaskDialog.Show("WallSplitter 경고",
                        "분리는 완료되었지만 일부 항목이 정상 처리되지 못했습니다:\n" + string.Join("\n", warnings));
                }

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // 사용자가 선택 중 ESC로 취소한 경우 - 오류가 아니므로 조용히 종료
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        // 원본 벽 하나의 CompoundStructure를 읽어, 실제로 단일 벽이 만들어질 레이어들의 오프셋/재료 정보를
        // 미리 계산한다. 두께 0(멤브레인) 레이어는 여기서 걸러지고 skipReasons에 사유가 남는다.
        // 문서를 전혀 수정하지 않는 순수 읽기 전용 계산이라 트랜잭션 밖에서 호출해도 안전하다 - Tx1의 벽 생성
        // 루프와 DirectType 모드의 유형 지정 창이 이 결과를 공유한다.
        private static List<LayerDescriptor> BuildLayerDescriptors(Document doc, WallPrepData prep, List<string> skipReasons)
        {
            List<LayerDescriptor> result = new List<LayerDescriptor>();

            // structure.GetLayers()는 항상 "외부(Exterior) → 내부(Interior)" 순서로 반환된다.
            // Wall.Orientation은 벽의 외부(바깥쪽)를 향하는 단위 법선 벡터이므로,
            // 외부 표면(exterior face)은 원래 중심선에서 +orientation * (totalWidth/2) 만큼 떨어진 위치가 된다.
            // 여기서부터 레이어 두께만큼씩 안쪽(-orientation 방향)으로 이동하며 각 레이어의 중심선을 구한다.
            double faceOffset = prep.TotalWidth / 2.0;

            foreach (CompoundStructureLayer layer in prep.Structure.GetLayers())
            {
                double layerWidth = layer.Width;
                double centerOffset = faceOffset - layerWidth / 2.0;
                faceOffset -= layerWidth;

                // 두께 0 레이어(멤브레인 등)는 두께 있는 벽 유형을 만들 수 없으므로 건너뜀
                if (layerWidth < MinLayerWidth)
                {
                    skipReasons.Add($"{prep.WallId}: 두께 0 레이어(멤브레인)는 건너뜀");
                    continue;
                }

                ElementId materialId = layer.MaterialId;
                string materialName = "지정되지않음";
                if (materialId != ElementId.InvalidElementId)
                {
                    Material mat = doc.GetElement(materialId) as Material;
                    materialName = mat?.Name ?? materialName;
                }

                result.Add(new LayerDescriptor
                {
                    LayerWidth = layerWidth,
                    CenterOffset = centerOffset,
                    MaterialId = materialId,
                    MaterialName = materialName,
                    Function = layer.Function,
                });
            }

            return result;
        }

        // 픽 대상을 벽으로만 제한하는 선택 필터
        private class WallSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Wall;
            public bool AllowReference(Reference reference, XYZ position) => true;
        }

        // 설정된 이름의 단일 레이어 벽 유형을 찾거나 생성한다.
        // 같은 이름의 기존 유형이 "정확히 같은 재료+두께의 단일 레이어 구조"일 때만 재사용한다.
        // (이름 형식 설정에서 재료명/두께를 빼면 서로 다른 레이어가 같은 이름으로 몰릴 수 있으므로 반드시 검증)
        private static WallType GetOrCreateSingleLayerWallType(
            Document doc, WallType sourceType, string baseName,
            double layerWidth, MaterialFunctionAssignment function, ElementId materialId)
        {
            string name = baseName;
            int suffix = 2;

            while (true)
            {
                WallType existing = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault(wt => wt.Name == name);

                if (existing == null)
                {
                    WallType created = sourceType.Duplicate(name) as WallType;

                    CompoundStructureLayer newLayer = new CompoundStructureLayer(layerWidth, function, materialId);
                    CompoundStructure newStructure = created.GetCompoundStructure();
                    newStructure.SetLayers(new List<CompoundStructureLayer> { newLayer });
                    created.SetCompoundStructure(newStructure);
                    return created;
                }

                IList<CompoundStructureLayer> existingLayers = existing.GetCompoundStructure()?.GetLayers();
                if (existingLayers != null && existingLayers.Count == 1
                    && existingLayers[0].MaterialId == materialId
                    && Math.Abs(existingLayers[0].Width - layerWidth) < 1e-9)
                {
                    return existing;
                }

                name = $"{baseName}({suffix++})";
            }
        }

        // 결합 재현 대상 한 쌍 (같은 재료로 만들어진, 원래 서로 결합되어 있던 두 벽의 해당 끝)
        private class JoinPair
        {
            public Wall WallA;
            public int EndA;
            public Wall WallB;
            public int EndB;
        }

        // 선택된 원본 벽들끼리 결합되어 있던 끝을 찾아, 양쪽 모두에 존재하는 벽 유형(=같은 재료+두께)의
        // 새 단일 벽 쌍 목록을 만든다. 문서 수정 없음(읽기 전용) - 트랜잭션 밖에서 호출 가능.
        private static List<JoinPair> CollectJoinPairs(
            List<WallPrepData> preps, Dictionary<(ElementId, ElementId), List<Wall>> createdMap)
        {
            List<JoinPair> pairs = new List<JoinPair>();
            HashSet<ElementId> selectedWallIds = new HashSet<ElementId>(preps.Select(p => p.WallId));
            // A->B와 B->A가 같은 결합점을 두 번 처리하지 않도록 (원본 벽 Id, 끝) 쌍을 정규화해 중복 제거
            HashSet<string> processedJoinEnds = new HashSet<string>();

            foreach (WallPrepData prepA in preps)
            {
                for (int endA = 0; endA < 2; endA++)
                {
                    foreach (ElementId joinedId in prepA.ElementsAtJoin[endA])
                    {
                        if (!selectedWallIds.Contains(joinedId)) continue;

                        WallPrepData prepB = preps.First(p => p.WallId == joinedId);

                        // prepB 쪽에서 prepA와 결합되어 있던 끝(end) 찾기
                        int endB = -1;
                        for (int e = 0; e < 2; e++)
                        {
                            if (prepB.ElementsAtJoin[e].Contains(prepA.WallId)) { endB = e; break; }
                        }
                        if (endB == -1) continue;

                        string keyA = $"{prepA.WallId}:{endA}";
                        string keyB = $"{prepB.WallId}:{endB}";
                        string pairKey = string.CompareOrdinal(keyA, keyB) < 0 ? $"{keyA}|{keyB}" : $"{keyB}|{keyA}";
                        if (!processedJoinEnds.Add(pairKey)) continue;

                        // 양쪽 원본 벽 모두에서 만들어진(=같은 재료+두께) 벽 유형만 대상
                        HashSet<ElementId> typeIdsB = new HashSet<ElementId>(
                            createdMap.Keys.Where(k => k.Item1 == prepB.WallId).Select(k => k.Item2));

                        foreach (var key in createdMap.Keys.Where(k => k.Item1 == prepA.WallId && typeIdsB.Contains(k.Item2)))
                        {
                            List<Wall> wallsA = createdMap[(prepA.WallId, key.Item2)];
                            List<Wall> wallsB = createdMap[(prepB.WallId, key.Item2)];
                            // 같은 재료 레이어가 한 벽 안에 두 번(대칭 복합 구조) 나오는 경우를 위해 순서대로 짝짓는다.
                            int count = Math.Min(wallsA.Count, wallsB.Count);
                            for (int i = 0; i < count; i++)
                                pairs.Add(new JoinPair { WallA = wallsA[i], EndA = endA, WallB = wallsB[i], EndB = endB });
                        }
                    }
                }
            }

            return pairs;
        }

        // 오프셋된(중심선에서 벗어난) 두 벽이 코너에서 자동으로 미터 결합되도록, 위치선을 실제 교차점까지
        // 연장/절단한다.
        //
        // 근본 원인: 각 레이어 벽의 끝점은 원본 벽 끝점을 "그대로 평행 이동"한 좌표라서, 서로 다른 만큼
        // 오프셋된 두 벽의 끝점은 코너에서 정확히 만나지 않는다 (오프셋이 0인, 즉 원본 중심선과 겹치는
        // 레이어만 우연히 원래 교차점과 일치해 Revit이 자동으로 결합/트림해 준다 - 사용자가 관찰한
        // "중심 벽체만 잘 결합되는" 현상이 바로 이것이다). WallUtils.AllowWallJoinAtEnd는 두 벽의 위치선이
        // 실제로 만나는 지점에서만 미터 처리를 하므로, 여기서 두 위치선의 교차점을 직접 계산해 끝점을 옮겨준다.
        // 직선 벽 쌍에서만 동작한다 (호 벽은 트림하지 않고 기존 끝점을 유지 - AllowWallJoinAtEnd만 시도됨).
        private static void TrimToIntersection(JoinPair pair)
        {
            if (!(pair.WallA.Location is LocationCurve locA) || !(locA.Curve is Line lineA)) return;
            if (!(pair.WallB.Location is LocationCurve locB) || !(locB.Curve is Line lineB)) return;

            XYZ fixedA = lineA.GetEndPoint(1 - pair.EndA);
            XYZ movingA = lineA.GetEndPoint(pair.EndA);
            XYZ fixedB = lineB.GetEndPoint(1 - pair.EndB);
            XYZ movingB = lineB.GetEndPoint(pair.EndB);

            XYZ dirAVec = movingA - fixedA;
            XYZ dirBVec = movingB - fixedB;
            if (dirAVec.GetLength() < 1e-9 || dirBVec.GetLength() < 1e-9) return;

            XYZ intersection = IntersectLinesXY(fixedA, dirAVec.Normalize(), fixedB, dirBVec.Normalize());
            if (intersection == null) return; // 평행한 벽은 애초에 코너를 이루지 않으므로 손대지 않음

            XYZ ptA0 = pair.EndA == 0 ? intersection : fixedA;
            XYZ ptA1 = pair.EndA == 0 ? fixedA : intersection;
            if (ptA0.DistanceTo(ptA1) > 1e-6)
                locA.Curve = Line.CreateBound(ptA0, ptA1);

            XYZ ptB0 = pair.EndB == 0 ? intersection : fixedB;
            XYZ ptB1 = pair.EndB == 0 ? fixedB : intersection;
            if (ptB0.DistanceTo(ptB1) > 1e-6)
                locB.Curve = Line.CreateBound(ptB0, ptB1);
        }

        // 두 직선(각각 한 점 + 단위 방향)의 XY 평면상 교차점. 평행이면 null (벽 위치선은 항상 같은 레벨의
        // 수평면 위에 있으므로 Z는 fixedA를 그대로 사용해도 무방하다).
        private static XYZ IntersectLinesXY(XYZ p1, XYZ d1, XYZ p2, XYZ d2)
        {
            double cross = d1.X * d2.Y - d1.Y * d2.X;
            if (Math.Abs(cross) < 1e-9) return null;
            XYZ diff = p2 - p1;
            double t = (diff.X * d2.Y - diff.Y * d2.X) / cross;
            return new XYZ(p1.X + t * d1.X, p1.Y + t * d1.Y, p1.Z);
        }

        // baseCurve를 orientation 방향으로 offset만큼 이동한 곡선을 반환.
        //
        // 직선(Line)은 검증된 단순 평행 이동을 사용한다 (Curve.CreateOffset 기반 버전은 실측 결과
        // 벽이 중심선 근처에 몰리는 등 신뢰할 수 없는 결과를 냈다 - 직선은 애초에 벡터 이동만으로
        // 충분하므로 더 단순하고 검증된 방식을 쓰는 게 맞다).
        //
        // 호(Arc)는 Curve.CreateOffset을 아예 쓰지 않는다. 이 API는 음수 거리를 넘기면
        // "Cannot create the offset of the curve" 예외를 던지고, 양수로 고쳐도 여전히 실패하는 것으로
        // 실측 확인되어(원인 불명, 문서화되지 않은 내부 제약으로 추정) 신뢰할 수 없다고 판단, 대신
        // OffsetArcPerpendicular에서 중심점·각도·기준축을 그대로 유지하고 반지름만 바꾼 동심원 호를
        // Arc.Create로 직접 재구성한다. Line과 마찬가지로 Revit이 제공하는 곡선 API를 신뢰하지 않고
        // 검증 가능한 값(Arc 자신의 Center/Radius/XDirection/YDirection, GetEndParameter)만으로 계산한다.
        private static Curve OffsetCurvePerpendicular(Curve baseCurve, double offset, XYZ orientation)
        {
            if (Math.Abs(offset) < 1e-9) return baseCurve.Clone();

            if (baseCurve is Line line)
            {
                XYZ translation = orientation * offset;
                return Line.CreateBound(line.GetEndPoint(0) + translation, line.GetEndPoint(1) + translation);
            }

            if (baseCurve is Arc arc)
                return OffsetArcPerpendicular(arc, offset, orientation);

            throw new InvalidOperationException("지원하지 않는 위치선 곡선 형식입니다 (Line/Arc만 지원).");
        }

        // 호(Arc)를 중심점·각도 범위·기준축은 그대로 두고 반지름만 조정해 오프셋한다 (동심원 재구성).
        // "반지름을 늘리는 쪽"과 "줄이는 쪽" 둘 다 만들어 보고, 실제로 중간점이 offset 부호와 같은
        // 방향으로 이동한 쪽만 채택한다 - 어느 쪽이 "바깥"인지 미리 가정하지 않고 결과로 검증한다.
        private static Arc OffsetArcPerpendicular(Arc arc, double offset, XYZ orientation)
        {
            XYZ center = arc.Center;
            double startParam = arc.GetEndParameter(0);
            double endParam = arc.GetEndParameter(1);
            XYZ baseMid = arc.Evaluate(0.5, true);

            Arc TryRadius(double r)
            {
                if (r < 1e-6) return null;
                Arc candidate = Arc.Create(center, r, startParam, endParam, arc.XDirection, arc.YDirection);
                XYZ candidateMid = candidate.Evaluate(0.5, true);
                double signedDistance = (candidateMid - baseMid).DotProduct(orientation);
                return signedDistance * offset > 0 ? candidate : null;
            }

            return TryRadius(arc.Radius + offset)
                ?? TryRadius(arc.Radius - offset)
                ?? throw new InvalidOperationException(
                    $"호 벽의 반지름({arc.Radius * 304.8:F0}mm)보다 큰 오프셋({Math.Abs(offset) * 304.8:F0}mm)은 만들 수 없습니다.");
        }

        // 호(Arc) 벽의 편집된 프로파일 곡선을, 오프셋된(반지름이 다른) 새 벽의 프로파일 위치로 옮기는 변환을 만든다.
        //
        // 근거: 동심원 오프셋이므로 origArc와 offsetArc는 중심점(Center)과 각도 범위가 완전히 같고 반지름만 다르다.
        // 따라서 원본 호 위의 한 점을 반지름 비율(scale = 새 반지름/원본 반지름)만큼 중심점 기준으로 방사형 확대·축소하면
        // 정확히 새 호 위의 대응점이 나온다 (반지름 R, scale s일 때 s*R = 새 반지름). 프로파일 곡선의 높이(Z)는
        // 평면 상 이동과 무관하므로 그대로 보존한다. 이 변환은 중심축(center를 지나는 수직선) 기준의
        // "수평 방향 반경 스케일 + 수직 방향은 항상 그대로"이며, 원본 벽의 프로파일 평면이 실제로 어떤 축 규칙을
        // 쓰는지에 대한 가정이 필요 없다 (직선 벽의 평행 이동 방식과 같은 원리 - 검증 가능한 기하 사실만 사용).
        //
        // 주의: 아직 실제 Revit에서 라이브 검증되지 않았다 - 프로파일에 포함된 곡선이 이 비등방(non-uniform,
        // Z축만 배율이 다름) 변환 하에서 유효한 도형으로 남지 않으면(예: 특정 각도의 타원형이 되는 경우)
        // Curve.CreateTransformed가 실패할 수 있으며, 그 경우 TryCopyProfileSketch가 폴리라인으로 대체한다.
        private static Transform BuildArcProfileMapping(Arc originalArc, Arc offsetArc)
        {
            if (originalArc == null || offsetArc == null) return null;
            if (originalArc.Radius < 1e-9) return null;

            double scale = offsetArc.Radius / originalArc.Radius;
            XYZ center = originalArc.Center;

            Transform mapping = Transform.Identity;
            mapping.BasisX = new XYZ(scale, 0, 0);
            mapping.BasisY = new XYZ(0, scale, 0);
            mapping.BasisZ = new XYZ(0, 0, 1);
            mapping.Origin = new XYZ(center.X * (1 - scale), center.Y * (1 - scale), 0);
            return mapping;
        }

        // 원본 벽의 편집된 프로파일(비직사각형 실루엣)을 새로 생성된 단일 벽에 재현한다.
        //
        // task.Mapping은 직선 벽이면 평행 이동, 호 벽이면 중심축 기준 반경 스케일이다 (ProfileTask 주석,
        // BuildArcProfileMapping 참고) - 둘 다 Revit이 내부적으로 프로파일 평면의 축을 어떻게 정의하는지에 대한
        // 가정 없이, 검증 가능한 기하 사실(직선: 중심선 이동량 자체, 호: 동심원 반지름 비율)만으로 유도된다.
        // (예전 버전은 원본/새 스케치 평면의 XVec·YVec 축이 같은 규칙이라고 가정하고 좌표계를 재매핑했는데,
        //  실측 결과 벽 높이 구속조건 충돌과 잘못된 형상을 유발해 이 방식으로 교체함.)
        //
        // 주의: SketchEditScope는 열려 있는 트랜잭션 안에서 시작할 수 없고,
        // 스코프 안의 문서 수정은 반드시 내부 트랜잭션으로 감싸야 한다. (Revit API 제약)
        // 실패 시 새 벽은 기본 사각형 프로파일로 남고, 실패 사유 문자열을 반환한다(성공 시 null).
        private static string TryCopyProfileSketch(Document doc, ProfileTask task)
        {
            SketchEditScope scope = new SketchEditScope(doc, "프로파일 복사");
            try
            {
                scope.Start(task.SketchId);

                using (Transaction tx = new Transaction(doc, "프로파일 곡선 재작성"))
                {
                    tx.Start();

                    Sketch sketch = doc.GetElement(task.SketchId) as Sketch;
                    SketchPlane sketchPlane = sketch.SketchPlane;

                    // 기본 사각형 프로파일 곡선 제거
                    foreach (ElementId elementId in sketch.GetAllElements())
                        doc.Delete(elementId);

                    foreach (CurveArray loop in task.OriginalProfile)
                    {
                        foreach (Curve curve in loop)
                        {
                            Curve mapped = TransformCurveOrFallback(curve, task.Mapping);
                            doc.Create.NewModelCurve(mapped, sketchPlane);
                        }
                    }

                    tx.Commit();
                }

                scope.Commit(new SilentFailuresPreprocessor());
                return null;
            }
            catch (Exception ex)
            {
                try { if (scope.IsActive) scope.Cancel(); } catch { /* 취소 실패 시에도 원래 예외를 보고 */ }
                return ex.Message;
            }
            finally
            {
                scope.Dispose();
            }
        }

        // curve를 mapping으로 변환한다. 직선 벽(평행 이동)에서는 항상 성공한다.
        // 호 벽(비등방 반경 스케일)에서는 원본 곡선이 Arc/Ellipse 등일 경우 변환 결과가 Revit이 표현할 수 없는
        // 도형이 되어 CreateTransformed가 실패할 수 있는데, 이 경우 곡선을 여러 점으로 나눈 뒤 각 점을
        // mapping.OfPoint로 개별 변환하고 짧은 직선들로 이어붙여 근사한다 (형태는 100% 정확하지 않을 수 있지만
        // 스케치가 아예 실패해 기본 사각형으로 되돌아가는 것보다는 원본 실루엣에 훨씬 가깝다).
        private static Curve TransformCurveOrFallback(Curve curve, Transform mapping)
        {
            try
            {
                return curve.CreateTransformed(mapping);
            }
            catch
            {
                IList<XYZ> points = curve.Tessellate();
                XYZ p0 = mapping.OfPoint(points[0]);
                XYZ p1 = mapping.OfPoint(points[points.Count - 1]);
                // NewModelCurve는 단일 Curve만 받으므로, 근사가 필요한 경우 양 끝점을 잇는 직선으로 단순화한다.
                // (다중 세그먼트로 쪼개려면 호출부에서 여러 ModelCurve를 만들어야 하므로, 우선 가장 보수적인
                //  형태로 폐곡선 연결이 깨지지 않게 양 끝점만 보존한다.)
                return Line.CreateBound(p0, p1);
            }
        }

        // 프로파일 스케치 커밋 중 발생하는 실패를 자동으로 처리해 대화상자가 뜨지 않도록 한다.
        // "구속조건이 충족되지 않습니다"(예: BuiltInFailures.SketchFailures 계열) 같은 오류는
        // FailureSeverity.Warning이 아니라 Error이므로 DeleteWarning으로는 처리되지 않고,
        // 그대로 두면 Revit이 사용자에게 해결 방법을 직접 고르라는 대화상자를 띄운다.
        // ResolveFailure는 그 대화상자에서 기본으로 강조 표시되는 해결책(대개 "구속조건 제거")을
        // 대화상자 없이 그대로 적용해 주므로, 경고/오류 구분 없이 해결책이 있는 모든 실패에 적용한다.
        private class SilentFailuresPreprocessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                foreach (FailureMessageAccessor f in failuresAccessor.GetFailureMessages())
                {
                    if (f.GetSeverity() == FailureSeverity.Warning)
                        failuresAccessor.DeleteWarning(f);
                    else if (f.HasResolutions())
                        failuresAccessor.ResolveFailure(f);
                }
                return FailureProcessingResult.ProceedWithCommit;
            }
        }

        // 원본 벽의 파라미터 값을 새로 생성된 벽에 그대로 복사 (높이/오프셋 등 상하 구속 조건 유지 목적)
        private static void CopyParam(Wall source, Wall target, BuiltInParameter bip)
        {
            Parameter srcParam = source.get_Parameter(bip);
            Parameter tgtParam = target.get_Parameter(bip);
            if (srcParam == null || tgtParam == null || tgtParam.IsReadOnly) return;

            switch (srcParam.StorageType)
            {
                case StorageType.Double:
                    tgtParam.Set(srcParam.AsDouble());
                    break;
                case StorageType.Integer:
                    tgtParam.Set(srcParam.AsInteger());
                    break;
                case StorageType.ElementId:
                    tgtParam.Set(srcParam.AsElementId());
                    break;
            }
        }
    }
}
