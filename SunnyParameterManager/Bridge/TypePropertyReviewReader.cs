using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using System.Globalization;

namespace SunnyParameterManager.Bridge;

internal static class TypePropertyReviewReader
{
    internal static TypePropertyReviewSnapshot Read(Document doc, TypePropertyReviewQuery query)
    {
        var result = new TypePropertyReviewSnapshot
        {
            FormatVersion = TypePropertyReviewSnapshot.CurrentFormatVersion
        };
        var projectInfo = query.ProjectParameters.Count > 0 ? doc.ProjectInformation : null;
        foreach (var field in query.ProjectParameters) result.ProjectValues.Add(field, ReadNamed(projectInfo, field));
        var fields = query.ElementParameters.Select(name => (Name: name, Key: TypePropertyReviewQuery.NamedElementKey(name), Ifc: false)).ToList();
        if (query.ReadIfc) fields.Add((TypePropertyReviewQuery.IfcField, TypePropertyReviewQuery.IfcField, true));
        var entries = new Dictionary<ElementId, (ElementType Element, TypePropertyEntry Entry, bool ReadAttributes)>();
        foreach (var type in new FilteredElementCollector(doc).WhereElementIsElementType().OfType<ElementType>())
        {
            if (type is RevitLinkType || type.Category?.CategoryType != CategoryType.Model) continue;
            var entry = new TypePropertyEntry
            {
                Name = type.Name, Family = type.FamilyName ?? "", Category = type.Category.Name,
                ElementId = type.Id.Value.ToString(CultureInfo.InvariantCulture)
            };
            entries.Add(type.Id, (type, entry, query.IncludesAttributes(entry.Name)));
        }
        if (entries.Count == 0) return result;
        var worksetTable = query.ReadWorkset && doc.IsWorkshared ? doc.GetWorksetTable() : null;
        foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            if (!entries.TryGetValue(element.GetTypeId(), out var saved)) continue;
            var entry = saved.Entry;
            entry.InstanceCount++;
            if (query.ReadWorkset)
            {
                if (!entry.InstanceValues.TryGetValue(TypePropertyReviewQuery.WorksetField, out var worksets))
                    entry.InstanceValues.Add(TypePropertyReviewQuery.WorksetField, worksets = []);
                worksets.Add(new TypePropertyValue(worksetTable?.GetWorkset(element.WorksetId)?.Name));
            }
            if (!saved.ReadAttributes) continue;
            foreach (var field in fields)
            {
                if (!entry.InstanceValues.TryGetValue(field.Key, out var values)) entry.InstanceValues.Add(field.Key, values = []);
                values.Add(ReadField(element, field.Name, false, field.Ifc));
            }
        }
        foreach (var saved in entries.Values)
        {
            if (saved.Entry.InstanceCount == 0 && !query.IncludeUnplaced) continue;
            if (saved.ReadAttributes)
                foreach (var field in fields)
                    saved.Entry.TypeValues.Add(field.Key, ReadField(saved.Element, field.Name, true, field.Ifc));
            result.Types.Add(saved.Entry);
        }
        return result;
    }

    private static TypePropertyValue ReadField(Element element, string field, bool type, bool readIfc)
    {
        if (readIfc)
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
