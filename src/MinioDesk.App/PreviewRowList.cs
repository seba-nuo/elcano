using System.ComponentModel;
using System.Data;

namespace MinioDesk.App;

// Preserve DataView's dynamic column descriptors while enabling a custom sort in WPF.
internal sealed class PreviewRowList(DataView source) : List<DataRowView>(source.Cast<DataRowView>()), ITypedList
{
    public PropertyDescriptorCollection GetItemProperties(PropertyDescriptor[]? listAccessors) => ((ITypedList)source).GetItemProperties(listAccessors);
    public string GetListName(PropertyDescriptor[]? listAccessors) => ((ITypedList)source).GetListName(listAccessors);
}
