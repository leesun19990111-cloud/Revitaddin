using Autodesk.Revit.DB;
namespace Autodesk.Revit.DB {
 public class ParameterElement:Element {public Definition Definition=new();public Definition GetDefinition()=>Definition;}
 public class SharedParameterElement:ParameterElement {public Guid GuidValue;}
 public enum ScheduleFieldType {Instance,ElementType,Count,Formula,ProjectInfo,RevitLinkType,RevitLinkInstance}
 public enum ScheduleFieldDisplayType {Standard,Totals}
 public enum ViewDuplicateOption {Duplicate}
 public enum SectionType {Body}
 public record ScheduleFieldId(int IntegerValue) {public override string ToString()=>IntegerValue.ToString();}
 public class ScheduleField {
  public FormatOptions Format=new();public FormatOptions GetFormatOptions()=>Format;
  public ScheduleFieldId FieldId=new(0);public ElementId ParameterId=new(-1);public ScheduleFieldType FieldType;
  public bool IsHidden,IsCalculatedField,IsCombinedParameterField;public ScheduleFieldDisplayType DisplayType;public string ColumnHeading="";
  public string GetName()=>ColumnHeading;public ScheduleField Clone()=>(ScheduleField)MemberwiseClone();
 }
 public class ScheduleSortGroupField {public ScheduleFieldId FieldId=new(0);public bool ShowHeader=true,ShowFooter=true,ShowBlankLine=true;public ScheduleSortGroupField Clone()=>(ScheduleSortGroupField)MemberwiseClone();}
 public class ScheduleFilter {public ScheduleFieldId FieldId=new(0);}
 public class ScheduleDefinition {
  public ElementId CategoryId=new(-1);
  public bool IsKeySchedule,IsMaterialTakeoff,HasEmbeddedSchedule,IncludeLinkedFiles,IsItemized=true,ShowTitle=true,ShowHeaders=true,ShowGrandTotal=true;
  public List<ScheduleField> Fields=new();public List<ScheduleSortGroupField> Sort=new();public List<ScheduleFilter> Filters=new();
  public IList<ScheduleFieldId> GetFieldOrder()=>Fields.Select(f=>f.FieldId).ToList();public ScheduleField GetField(ScheduleFieldId id)=>Fields.Single(f=>f.FieldId==id);
  public IList<ScheduleSortGroupField> GetSortGroupFields()=>Sort;public void SetSortGroupFields(IList<ScheduleSortGroupField> list)=>Sort=list.ToList();public IList<ScheduleFilter> GetFilters()=>Filters;
  public ScheduleField AddField(ScheduleFieldType type)=>AddField(type,new ElementId(-1));
  public ScheduleField AddField(ScheduleFieldType type,ElementId parameter){var f=new ScheduleField{FieldId=new(Fields.Select(f=>f.FieldId.IntegerValue).DefaultIfEmpty(0).Max()+1),FieldType=type,ParameterId=parameter};Fields.Add(f);return f;}
  public ScheduleDefinition Clone(){var c=(ScheduleDefinition)MemberwiseClone();c.Fields=Fields.Select(f=>f.Clone()).ToList();c.Sort=Sort.Select(s=>s.Clone()).ToList();c.Filters=Filters.ToList();return c;}
 }
 public class ViewSchedule:View {
  public Document Owner=null!;public ElementId ViewTemplateId=new(100);public ScheduleDefinition Definition=new();public List<Element> Candidates=new();public Dictionary<int,List<Element>> LinkedCandidates=new();
  public List<Dictionary<string,string>> NativeRows=new();public List<int> Counts=new();
  public ElementId Duplicate(ViewDuplicateOption _){var v=(ViewSchedule)MemberwiseClone();v.Id=new(99000);v.Definition=Definition.Clone();Owner.Elements.Add(v);return v.Id;}
  public TableData GetTableData()=>new(this);
  public string GetCellText(SectionType _,int row,int col){var f=Definition.Fields.Where(f=>!f.IsHidden).ElementAt(col);return f.FieldType==ScheduleFieldType.Count?Counts[row].ToString():NativeRows[row].GetValueOrDefault(f.FieldId.ToString())??NativeRows[row].GetValueOrDefault("param:"+f.ParameterId)??"";}
 }
 public class TableData(ViewSchedule view){public TableSectionData GetSectionData(SectionType _)=>new(view);}
 public class TableSectionData(ViewSchedule view){public int NumberOfColumns=>view.Definition.Fields.Count(f=>!f.IsHidden);public int NumberOfRows=>view.NativeRows.Count;public int FirstRowNumber=>0;public int LastRowNumber=>NumberOfRows-1;public int FirstColumnNumber=>0;}
}
namespace Autodesk.Revit.DB {
 public class Level:Element {}
 public class Units {}
 public class FormatOptions:IDisposable {public bool UseDefault=true;public int Digits=2;public void Dispose(){}}
 public class FormatValueOptions:IDisposable {public FormatOptions? Format;public void SetFormatOptions(FormatOptions f)=>Format=f;public void Dispose(){}}
 public static class UnitUtils {public static bool IsMeasurableSpec(ForgeTypeId id)=>id.TypeId=="number";}
 public static class UnitFormatUtils {public static string Format(Units units,ForgeTypeId spec,double value,bool editing,FormatValueOptions options)=>options.Format==null?value.ToString():value.ToString("F"+options.Format.Digits);}
 public static class LabelUtils {public static string GetLabelForUnit(ForgeTypeId id)=>id.TypeId;}
}
