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
    // SplitWallCommand(Class1.cs)와 동일한 설계를 바닥(Floor)에 적용한 것 - 선택 방식, NamingSettings
    // (템플릿/유형 직접 지정 + 단일·복수 지속), 유형 재사용/생성 검증 로직까지 전부 공유한다.
    // 다만 벽과는 오프셋 방향과 프로파일 처리 방식이 근본적으로 다르다 (아래 BuildLayerDescriptors 주석 참고):
    //   - 벽: 레이어를 중심선 기준 수평(perpendicular)으로 오프셋, 위치선(LocationCurve)이 곡선.
    //   - 바닥: 레이어를 최상단 기준 수직(Z)으로 오프셋, 위치는 항상 경계 스케치(SketchId)의 CurveLoop.
    // 이 차이 때문에 벽에 있던 "프로파일 편집 여부 분기"(HasEditedProfile)와 "코너 트림 후 재결합" 단계는
    // 바닥에는 아예 없다 - 모든 바닥은 항상 경계 스케치를 가지고, 레이어들은 같은 평면(X,Y) 위에 Z만 다르게
    // 쌓이므로 코너가 어긋날 일이 없다. 대신 Floor.Create가 경계 CurveLoop를 직접 받으므로
    // SketchEditScope로 커밋 후 따로 곡선을 다시 그려 넣는 2차 단계 자체가 필요 없다(1개 트랜잭션으로 끝).
    //
    // 검증 필요 사항(라이브 테스트 전이라 가정 단계임을 명시): CompoundStructure.GetLayers()가 바닥에서도
    // "Layer 1=위(외부 측)→아래(내부 측)" 순서라는 것과, Sketch.Profile 곡선의 Z가 바닥의 "윗면"(마감 상단)
    // 좌표라는 것 - 둘 다 Revit의 조립 편집(Edit Assembly) 대화상자 관례와 벽과 동일한 CompoundStructure API를
    // 근거로 한 판단이며, MetadataLoadContext로 API 시그니처까지는 확인했지만 실제 지오메트리 방향은
    // 라이브로 재검증 전이다. 반대로 뒤집혀 있는 것으로 확인되면 BuildLayerDescriptors의 deltaZ 부호만 뒤집으면 된다.
    [Transaction(TransactionMode.Manual)]
    public class SplitFloorCommand : IExternalCommand
    {
        private const double MinLayerWidth = 1e-9;

        private class FloorPrepData
        {
            public Floor Floor = null!;
            public ElementId FloorId = null!;
            public ElementId LevelId = null!;
            public CompoundStructure Structure = null!;
            // 원본 바닥의 "레벨로부터의 높이 오프셋"(Height Offset From Level) 파라미터 값(ft) - 새로 만드는
            // 레이어별 바닥은 이 값에서 레이어 깊이(TopOffset)만큼을 뺀 값으로 같은 파라미터를 명시적으로 설정한다.
            public double OriginalHeightOffset;
            // 원본 경계(원본 바닥 경계 스케치의 실제 Z 그대로, 홀 포함 가능) - 최상단 레이어의 윗면 Z로 가정.
            public List<CurveLoop> BoundaryLoops = new List<CurveLoop>();
            public List<FloorLayerDescriptor> LayerDescriptors = new List<FloorLayerDescriptor>();
            public List<ElementId> DirectTypeIds = new List<ElementId>();
        }

        // 바닥 레이어 하나에서 실제로 단일 바닥을 만드는 데 필요한 정보 (두께 0 레이어는 제외).
        private class FloorLayerDescriptor
        {
            public double LayerWidth;  // ft
            public double TopOffset;   // ft, 원본 바닥 윗면(TopZ)에서 이 레이어 윗면까지의 하강 깊이 (항상 0 이상)
            public ElementId MaterialId = ElementId.InvalidElementId;
            public string MaterialName = "";
            public MaterialFunctionAssignment Function;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                // [선택 단계] 벽체 분리와 동일: 미리 선택돼 있으면 그대로, 없으면 그때만 직접 고르게 한다.
                Selection sel = uiDoc.Selection;
                List<Floor> candidateFloors = new List<Floor>();

                ICollection<ElementId> preSelectedIds = sel.GetElementIds();
                if (preSelectedIds.Count > 0)
                {
                    foreach (ElementId id in preSelectedIds)
                        if (doc.GetElement(id) is Floor preSelectedFloor)
                            candidateFloors.Add(preSelectedFloor);
                }
                else
                {
                    IList<Reference> pickedRefs = sel.PickObjects(
                        ObjectType.Element,
                        new FloorSelectionFilter(),
                        "분리할 복합 바닥을 선택하세요 (여러 개 선택 가능). 완료 후 Enter 또는 완료 버튼.");

                    if (pickedRefs.Count == 0)
                        return Result.Cancelled;

                    foreach (Reference pickedRef in pickedRefs)
                        if (doc.GetElement(pickedRef) is Floor pickedFloor)
                            candidateFloors.Add(pickedFloor);
                }

                List<FloorPrepData> preps = new List<FloorPrepData>();
                List<string> skipReasons = new List<string>();

                foreach (Floor floor in candidateFloors)
                {
                    FloorType floorType = floor.FloorType;
                    CompoundStructure structure = floorType.GetCompoundStructure();
                    if (structure == null)
                    {
                        skipReasons.Add($"{floor.Id}: 레이어 구조 정보가 없어 건너뜀");
                        continue;
                    }

                    // 경사/형태 편집된 바닥(배수 구배 등)은 레이어별로 같은 형태 편집을 재현할 방법이 없어 제외.
                    // 벽의 "프로파일 편집"보다 근본적으로 더 복잡한 개념(정점/능선 단위 편집)이라 이번 범위 밖.
                    // Floor.SlabShapeEditor: 2023은 프로퍼티, 2024부터는 GetSlabShapeEditor() 메서드로 개명됨
                    // (MetadataLoadContext로 5개 연도 패키지를 모두 리플렉션해 확인 - csproj의 DefineConstants 참고).
#if REVIT2023
                    SlabShapeEditor shapeEditor = floor.SlabShapeEditor;
#else
                    SlabShapeEditor shapeEditor = floor.GetSlabShapeEditor();
#endif
                    if (shapeEditor != null && shapeEditor.IsEnabled)
                    {
                        skipReasons.Add($"{floor.Id}: 경사/형태 편집된 바닥(구배 슬래브)은 지원하지 않아 건너뜀");
                        continue;
                    }

                    if (floor.SketchId == ElementId.InvalidElementId || !(doc.GetElement(floor.SketchId) is Sketch sketch))
                    {
                        skipReasons.Add($"{floor.Id}: 경계 스케치를 읽을 수 없어 건너뜀");
                        continue;
                    }

                    List<CurveLoop> boundaryLoops = new List<CurveLoop>();
                    foreach (CurveArray loop in sketch.Profile)
                    {
                        List<Curve> curves = new List<Curve>();
                        foreach (Curve curve in loop) curves.Add(curve);
                        if (curves.Count == 0) continue;

                        boundaryLoops.Add(CurveLoop.Create(curves));
                    }

                    if (boundaryLoops.Count == 0)
                    {
                        skipReasons.Add($"{floor.Id}: 경계 곡선을 읽을 수 없어 건너뜀");
                        continue;
                    }

                    Parameter? heightOffsetParam = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                    double originalHeightOffset = heightOffsetParam?.AsDouble() ?? 0;

                    preps.Add(new FloorPrepData
                    {
                        Floor = floor,
                        FloorId = floor.Id,
                        LevelId = floor.LevelId,
                        Structure = structure,
                        OriginalHeightOffset = originalHeightOffset,
                        BoundaryLoops = boundaryLoops,
                    });
                }

                if (preps.Count == 0)
                {
                    TaskDialog.Show("오류", "분리 가능한 복합 바닥이 없습니다.\n" + string.Join("\n", skipReasons));
                    return Result.Cancelled;
                }

                // "설정" 버튼에서 저장해 둔 단일 벽/바닥 유형 이름 형식/지정 방식 - 벽체 분리와 완전히 공유.
                NamingSettings namingSettings = NamingSettings.Load();

                foreach (FloorPrepData prep in preps)
                    prep.LayerDescriptors = BuildLayerDescriptors(doc, prep, skipReasons);

                // [유형 지정 단계] DirectType 모드면 Tx를 열기 전에 원본 바닥마다 레이어별 유형을 확정해 둔다.
                // (모달 창은 열린 트랜잭션 안에서 띄우지 않는다 - NamerCommand/SplitWallCommand와 같은 원칙)
                if (namingSettings.Mode == NamingMode.DirectType)
                {
                    List<ElementType> availableTypes = new FilteredElementCollector(doc)
                        .OfClass(typeof(FloorType))
                        .Cast<ElementType>()
                        .OrderBy(t => t.Name)
                        .ToList();

                    foreach (FloorPrepData prep in preps)
                    {
                        if (prep.LayerDescriptors.Count == 0) continue; // 전부 멤브레인이라 만들 바닥이 없음

                        List<(ElementId MaterialId, double WidthFeet)> signature = prep.LayerDescriptors
                            .Select(d => (d.MaterialId, d.LayerWidth))
                            .ToList();

                        bool reusable = false;
                        List<ElementId> reusedIds = new List<ElementId>();
                        string? mismatchReason = null;

                        if (namingSettings.TypeAssignmentPersistence == TypeAssignmentPersistence.Multiple)
                            reusable = TypeAssignmentSession.TryGetReusableSelection(TypeAssignmentTarget.Floor, doc, signature, out reusedIds, out mismatchReason);

                        if (reusable)
                        {
                            prep.DirectTypeIds = reusedIds;
                            continue;
                        }

                        string? warningMessage = namingSettings.TypeAssignmentPersistence == TypeAssignmentPersistence.Multiple
                            ? mismatchReason
                            : null;

                        List<LayerPickItem> items = prep.LayerDescriptors
                            .Select((d, i) => new LayerPickItem { Index = i, MaterialName = d.MaterialName, ThicknessMm = d.LayerWidth * 304.8 })
                            .ToList();

                        string header = $"'{prep.Floor.FloorType.Name}' 바닥 분리 - 레이어 {items.Count}개에 적용할 유형을 검색하여 지정하세요.";

                        LayerTypeAssignmentWindow pickerWindow = new LayerTypeAssignmentWindow(header, items, availableTypes, warningMessage);
                        new WindowInteropHelper(pickerWindow) { Owner = commandData.Application.MainWindowHandle };
                        if (pickerWindow.ShowDialog() != true)
                            return Result.Cancelled; // 취소 시 바닥 분리 전체 작업을 취소 (벽체 분리와 동일한 규칙)

                        List<ElementId> resolvedIds = pickerWindow.Result.Select(t => t.Id).ToList();
                        prep.DirectTypeIds = resolvedIds;

                        if (namingSettings.TypeAssignmentPersistence == TypeAssignmentPersistence.Multiple)
                            TypeAssignmentSession.Remember(TypeAssignmentTarget.Floor, doc, signature, resolvedIds);
                    }
                }

                // [단일 트랜잭션] 벽과 달리 프로파일 복사/재결합 단계가 없어 트랜잭션이 하나면 충분하다.
                using (Transaction tx = new Transaction(doc, "단일 바닥 유형 생성 및 바닥 분리"))
                {
                    tx.Start();

                    foreach (FloorPrepData prep in preps)
                    {
                        for (int layerIndex = 0; layerIndex < prep.LayerDescriptors.Count; layerIndex++)
                        {
                            FloorLayerDescriptor d = prep.LayerDescriptors[layerIndex];

                            FloorType targetFloorType;
                            string newTypeName;

                            if (namingSettings.Mode == NamingMode.DirectType)
                            {
                                // [유형 단계 - 직접 지정] 위에서 이미 확정해 둔, 문서에 있는 기존 유형을 그대로 쓴다.
                                targetFloorType = (FloorType)doc.GetElement(prep.DirectTypeIds[layerIndex]);
                                newTypeName = targetFloorType.Name;
                            }
                            else
                            {
                                // [유형 단계 - 이름 형식] 설정된 이름 형식으로 단일 바닥 유형을 찾거나 생성.
                                newTypeName = namingSettings.BuildName(d.MaterialName, d.LayerWidth * 304.8);
                                targetFloorType = GetOrCreateSingleLayerFloorType(
                                    doc, prep.Floor.FloorType, newTypeName, d.LayerWidth, d.Function, d.MaterialId);
                            }

                            // [생성 단계] Floor.Create(slopeArrow=null, slope=0인 평바닥)는 프로파일 곡선의 Z를
                            // 무시하고 항상 "레벨로부터의 높이 오프셋" 파라미터(기본값 0, 즉 레벨과 동일 높이)로만
                            // 높이를 정한다 - 라이브 테스트로 확인됨(2026-07-13, 곡선을 Z로 미리 이동시켜서
                            // 만들어도 분리된 바닥이 두께와 무관하게 전부 레벨에 딱 붙어 생성되는 것으로 재현).
                            // 그래서 원본 경계 곡선은 그대로 쓰고, 생성 직후 그 파라미터를 명시적으로 설정해야
                            // 실제로 레이어 두께만큼 오프셋된다.
                            Floor newFloor = Floor.Create(doc, prep.BoundaryLoops, targetFloorType.Id, prep.LevelId, /*isStructural*/ false, /*slopeArrow*/ null!, /*slope*/ 0);

                            Parameter? offsetParam = newFloor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                            offsetParam?.Set(prep.OriginalHeightOffset - d.TopOffset);
                        }
                    }

                    // 원본 복합 바닥은 새 바닥들을 모두 만든 뒤 삭제
                    foreach (FloorPrepData prep in preps)
                        doc.Delete(prep.FloorId);

                    tx.Commit();
                }

                if (skipReasons.Count > 0)
                {
                    TaskDialog.Show("WallSplitter 경고",
                        "분리는 완료되었지만 일부 항목이 정상 처리되지 못했습니다:\n" + string.Join("\n", skipReasons));
                }

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        // 픽 대상을 바닥으로만 제한하는 선택 필터
        private class FloorSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Floor;
            public bool AllowReference(Reference reference, XYZ position) => true;
        }

        // 원본 바닥 하나의 CompoundStructure를 읽어, 실제로 단일 바닥이 만들어질 레이어들의 깊이/재료 정보를
        // 미리 계산한다. 두께 0(멤브레인) 레이어는 여기서 걸러지고 skipReasons에 사유가 남는다.
        // 문서를 전혀 수정하지 않는 순수 읽기 전용 계산이라 트랜잭션 밖에서 호출해도 안전하다.
        private static List<FloorLayerDescriptor> BuildLayerDescriptors(Document doc, FloorPrepData prep, List<string> skipReasons)
        {
            List<FloorLayerDescriptor> result = new List<FloorLayerDescriptor>();

            // structure.GetLayers()는 벽과 마찬가지로 "Layer 1(외부 측)→...→마지막(내부 측)" 순서로 반환된다.
            // 바닥에서는 이 "외부 측"이 윗면(마감 상단, 원본 경계 스케치의 Z)에 대응한다고 가정하고,
            // 여기서부터 레이어 두께만큼씩 아래(-Z 방향)로 내려가며 각 레이어의 윗면 깊이를 구한다.
            double runningDepth = 0;

            foreach (CompoundStructureLayer layer in prep.Structure.GetLayers())
            {
                double layerWidth = layer.Width;
                double topOffset = runningDepth;
                runningDepth += layerWidth;

                // 두께 0 레이어(멤브레인 등)는 두께 있는 바닥 유형을 만들 수 없으므로 건너뜀
                if (layerWidth < MinLayerWidth)
                {
                    skipReasons.Add($"{prep.FloorId}: 두께 0 레이어(멤브레인)는 건너뜀");
                    continue;
                }

                ElementId materialId = layer.MaterialId;
                string materialName = "지정되지않음";
                if (materialId != ElementId.InvalidElementId)
                {
                    Material? mat = doc.GetElement(materialId) as Material;
                    materialName = mat?.Name ?? materialName;
                }

                result.Add(new FloorLayerDescriptor
                {
                    LayerWidth = layerWidth,
                    TopOffset = topOffset,
                    MaterialId = materialId,
                    MaterialName = materialName,
                    Function = layer.Function,
                });
            }

            return result;
        }

        // 설정된 이름의 단일 레이어 바닥 유형을 찾거나 생성한다. GetOrCreateSingleLayerWallType(Class1.cs)과
        // 완전히 같은 규칙: 같은 이름의 기존 유형이 있어도 재료/두께가 다르면 재사용하지 않고 (2), (3)…을 붙인다.
        private static FloorType GetOrCreateSingleLayerFloorType(
            Document doc, FloorType sourceType, string baseName,
            double layerWidth, MaterialFunctionAssignment function, ElementId materialId)
        {
            string name = baseName;
            int suffix = 2;

            while (true)
            {
                FloorType? existing = new FilteredElementCollector(doc)
                    .OfClass(typeof(FloorType))
                    .Cast<FloorType>()
                    .FirstOrDefault(ft => ft.Name == name);

                if (existing == null)
                {
                    FloorType created = (FloorType)sourceType.Duplicate(name);

                    CompoundStructureLayer newLayer = new CompoundStructureLayer(layerWidth, function, materialId);
                    CompoundStructure newStructure = created.GetCompoundStructure();
                    newStructure.SetLayers(new List<CompoundStructureLayer> { newLayer });
                    created.SetCompoundStructure(newStructure);
                    return created;
                }

                IList<CompoundStructureLayer>? existingLayers = existing.GetCompoundStructure()?.GetLayers();
                if (existingLayers != null && existingLayers.Count == 1
                    && existingLayers[0].MaterialId == materialId
                    && Math.Abs(existingLayers[0].Width - layerWidth) < 1e-9)
                {
                    return existing;
                }

                name = $"{baseName}({suffix++})";
            }
        }
    }
}
