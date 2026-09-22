"""校验帮助文档的 xaml 是否能被解析（XML 层面）+ 检查 PCL 自定义控件的用法。"""
import os
import re
import xml.etree.ElementTree as ET

SRC = r"E:\DSH-PCL\dist\help-src\DSH"

# PCL 帮助页可用的自定义控件（从已有文档里统计）
KNOWN = {
    "MyCard", "MyHint", "MyImage", "MyButton", "MyCheckBox", "MyComboBox",
    "MyRadioBox", "MySlider", "MyTextButton", "MyIconButton", "MyIconTextButton",
    "MyLoading", "MyScrollViewer", "MyExtraTextButton", "MySearchBox",
    "MyListItem", "MyListItemButton", "MyMsgText", "MyPageLeft", "MyPageRight",
}
# WPF 原生标签
WPF = {
    "StackPanel", "Grid", "TextBlock", "Border", "Image", "Button", "WrapPanel",
    "DockPanel", "Canvas", "ScrollViewer", "Rectangle", "Ellipse", "Path",
    "ColumnDefinition", "RowDefinition", "Grid.ColumnDefinitions",
    "Grid.RowDefinitions", "Run", "Span", "Line", "Polygon", "Polyline",
    "Viewbox", "Separator", "Expander", "TabControl", "TabItem", "GroupBox",
    "ListBox", "ListView", "TreeView", "ProgressBar", "Slider", "CheckBox",
    "RadioButton", "ComboBox", "TextBox", "PasswordBox", "Label", "Hyperlink",
    "ToolTip", "InkCanvas", "MediaElement", "WebBrowser", "FlowDocumentReader",
}

files = sorted(f for f in os.listdir(SRC) if f.endswith(".xaml"))
print("待校验 xaml: %d 个" % len(files))
print()

all_ok = True
for fn in files:
    path = os.path.join(SRC, fn)
    raw = open(path, encoding="utf-8").read()

    # 1) XML 合法性：包一层根节点再解析
    try:
        ET.fromstring("<root xmlns:local='clr-namespace:PCL'>" + raw + "</root>")
        xml_ok = True
    except ET.ParseError as e:
        xml_ok = False
        all_ok = False
        print("  [XML ERROR] %s" % fn)
        print("      %s" % e)

    # 2) 标签名检查
    tags = set(re.findall(r"<(?:local:)?([A-Za-z][A-Za-z0-9_.]*)", raw))
    unknown = sorted(t for t in tags
                     if t not in KNOWN and t not in WPF and not t.startswith("local"))
    # 3) 属性检查：PCL 的 MyHint 用 Text/Theme/IsWarn，不要用 Content
    bad_attr = []
    if "MyHint" in raw and re.search(r"<local:MyHint[^>]*\sContent=", raw):
        bad_attr.append("MyHint 用了 Content（应该用 Text）")

    status = "OK" if (xml_ok and not unknown and not bad_attr) else "CHECK"
    print("  [%-5s] %-34s %6d bytes" % (status, fn, len(raw.encode("utf-8"))))
    if unknown:
        print("          未知标签: %s" % ", ".join(unknown))
    for b in bad_attr:
        print("          属性问题: %s" % b)

print()
print("总体:", "全部通过" if all_ok else "有问题，见上")
