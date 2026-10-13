using Autodesk.Revit.DB;
namespace Autodesk.Revit.DB {
 public class SharedParameterElement:Element {public Guid GuidValue;}
 public enum ScheduleFieldType {Instance,ElementType,Count,Formula,ProjectInfo}
 public enum ScheduleFieldDisplayType {Standard,Totals}
 public enum ViewDuplicateOption {Duplicate}
 public enum SectionType {Body}
 public record ScheduleFieldId(int IntegerValue) {public override string ToString()=>IntegerValue.ToString();}
 public class ScheduleField {
  public ScheduleFieldId FieldId=new(0);public ElementId ParameterId=new(-1);public ScheduleFieldType FieldType;
  public bool IsHidden,IsCalculatedField,IsCombinedParameterField;public ScheduleFieldDisplayType DisplayType;public string ColumnHeading="";
  public string GetName()=>ColumnHeading;public ScheduleField Clone()=>(ScheduleField)MemberwiseClone();
 }
 public class ScheduleSortGroupField {public ScheduleFieldId FieldId=new(0);public bool ShowHeader=true,ShowFooter=true,ShowBlankLine=true;public ScheduleSortGroupField Clone()=>(ScheduleSortGroupField)MemberwiseClone();}
 public class ScheduleFilter {public ScheduleFieldId FieldId=new(0);}
 public class ScheduleDefinition {
  public bool IsKeySchedule,IsMaterialTakeoff,HasEmbeddedSchedule,IncludeLinkedFiles,IsItemized=true,ShowTitle=true,ShowHeaders=true,ShowGrandTotal=true;
  public List<ScheduleField> Fields=new();public List<ScheduleSortGroupField> Sort=new();public List<ScheduleFilter> Filters=new();
  public IList<ScheduleFieldId> GetFieldOrder()=>Fields.Select(f=>f.FieldId).ToList();public ScheduleField GetField(ScheduleFieldId id)=>Fields.Single(f=>f.FieldId==id);
  public IList<ScheduleSortGroupField> GetSortGroupFields()=>Sort;public void SetSortGroupFields(IList<ScheduleSortGroupField> list)=>Sort=list.ToList();public IList<ScheduleFilter> GetFilters()=>Filters;
  public ScheduleField AddField(ScheduleFieldType type){var f=new ScheduleField{FieldId=new(99),FieldType=type};Fields.Add(f);return f;}
  public ScheduleDefinition Clone(){var c=(ScheduleDefinition)MemberwiseClone();c.Fields=Fields.Select(f=>f.Clone()).ToList();c.Sort=Sort.Select(s=>s.Clone()).ToList();c.Filters=Filters.ToList();return c;}
 }
 public class ViewSchedule:View {
  public Document Owner=null!;public ElementId ViewTemplateId=new(100);public ScheduleDefinition Definition=new();public List<Element> Candidates=new();public Dictionary<int,List<Element>> LinkedCandidates=new();
  public List<Dictionary<string,string>> NativeRows=new();public List<int> Counts=new();
  public ElementId Duplicate(ViewDuplicateOption _){var v=(ViewSchedule)MemberwiseClone();v.Id=new(99000);v.Definition=Definition.Clone();Owner.Elements.Add(v);return v.Id;}
  public TableData GetTableData()=>new(this);
  public string GetCellText(SectionType _,int row,int col){var f=Definition.Fields.Where(f=>!f.IsHidden).ElementAt(col);return f.FieldType==ScheduleFieldType.Count?Counts[row].ToString():NativeRows[row].GetValueOrDefault(f.FieldId.ToString())??"";}
 }
 public class TableData(ViewSchedule view){public TableSectionData GetSectionData(SectionType _)=>new(view);}
 public class TableSectionData(ViewSchedule view){public int NumberOfColumns=>view.Definition.Fields.Count(f=>!f.IsHidden);public int NumberOfRows=>view.NativeRows.Count;public int FirstRowNumber=>0;public int LastRowNumber=>NumberOfRows-1;public int FirstColumnNumber=>0;}
}
