using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using System.Globalization;

namespace SunnyParameterManager.Bridge;

internal static class TypePropertyReviewReader
{
    internal static TypePropertyReviewSnapshot Read(Document doc)
    {
        var result = new TypePropertyReviewSnapshot
        {
            Airport = ReadNamed(doc.ProjectInformation, "공항코드"),
            Building = ReadNamed(doc.ProjectInformation, "건축물번호")
        };
        var entries = new Dictionary<ElementId, TypePropertyEntry>();
        foreach (var type in new FilteredElementCollector(doc).WhereElementIsElementType().OfType<ElementType>())
        {
            if (type is RevitLinkType || type.Category?.CategoryType != CategoryType.Model || !TypePropertyReview.IncludesType(type.Name)) continue;
            var entry = new TypePropertyEntry
            {
                Name = type.Name, Family = type.FamilyName ?? "", Category = type.Category.Name,
                ElementId = type.Id.Value.ToString(CultureInfo.InvariantCulture)
            };
            foreach (var field in TypePropertyReview.ElementFields) entry.TypeValues.Add(field, ReadField(type, field, true));
            entries.Add(type.Id, entry);
        }
        if (entries.Count == 0) return result;
        foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            if (!entries.TryGetValue(element.GetTypeId(), out var entry)) continue;
            entry.InstanceCount++;
            if (!entry.InstanceValues.TryGetValue("작업세트", out var worksets)) entry.InstanceValues.Add("작업세트", worksets = []);
            var name = doc.IsWorkshared ? doc.GetWorksetTable().GetWorkset(element.WorksetId)?.Name : null;
            worksets.Add(new TypePropertyValue(name));
            foreach (var field in TypePropertyReview.ElementFields)
            {
                if (!entry.InstanceValues.TryGetValue(field, out var values)) entry.InstanceValues.Add(field, values = []);
                values.Add(ReadField(element, field, false));
            }
        }
        result.Types = entries.Values.ToList();
        return result;
    }

    private static TypePropertyValue ReadField(Element element, string field, bool type)
    {
        if (field == "다음으로 IFC로 내보내기")
        {
            var parameter = element.get_Parameter(type ? BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE_AS : BuiltInParameter.IFC_EXPORT_ELEMENT_AS);
            if (parameter != null) return ReadValue(parameter);
        }
        return ReadNamed(element, field);
    }

    private static TypePropertyValue ReadNamed(Element? element, string field)
    {
        if (element == null) return new();
        var parameters = element.GetParameters(field);
        return parameters.Count switch
        {
            0 => new(),
            1 => ReadValue(parameters[0]),
            _ => new(Ambiguous: true)
        };
    }

    private static TypePropertyValue ReadValue(Parameter parameter)
    {
        if (!parameter.HasValue) return new("");
        var value = parameter.StorageType switch
        {
            StorageType.String => parameter.AsString(),
            StorageType.Integer => parameter.AsValueString() ?? parameter.AsInteger().ToString(CultureInfo.InvariantCulture),
            StorageType.Double => parameter.AsValueString() ?? parameter.AsDouble().ToString(CultureInfo.InvariantCulture),
            StorageType.ElementId => parameter.AsValueString() ?? (parameter.AsElementId() == ElementId.InvalidElementId ? "" :
                parameter.AsElementId().Value.ToString(CultureInfo.InvariantCulture)),
            _ => ""
        };
        return new((value ?? "").Trim());
    }
}
