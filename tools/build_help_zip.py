"""把 help-src 目录打包成 PCL 的 Help.zip。

PCL 的帮助格式：
  <分类>/<标题>.json   —— 索引（Title / Description / Types 等）
  <分类>/<标题>.xaml   —— 内容（PCL 自定义控件的 WPF 片段）

HelpLoad 的扫描规则（Modules/ModMain.vb）：
  · 只认 .json 结尾的文件
  · 跳过路径里含 "\." 的目录（隐藏目录）
  · 每个 .json 对应同名的 .xaml（IsEvent 为 true 时不需要 xaml）
"""
import os
import zipfile

SRC = r"E:\DSH-PCL\dist\help-src"
OUT = r"E:\DSH-PCL\dist\help-build\Help.zip"

os.makedirs(os.path.dirname(OUT), exist_ok=True)

files = []
for root, dirs, names in os.walk(SRC):
    # 跳过隐藏目录（PCL 会忽略它们）
    dirs[:] = [d for d in dirs if not d.startswith(".")]
    for n in sorted(names):
        if n.startswith("."):
            continue
        full = os.path.join(root, n)
        rel = os.path.relpath(full, SRC).replace("\\", "/")
        files.append((full, rel))

files.sort(key=lambda x: x[1])

with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED) as z:
    for full, rel in files:
        z.write(full, rel)
        print("  + %s" % rel)

size = os.path.getsize(OUT)
print()
print("打包完成: %s  (%d bytes, %d 个文件)" % (OUT, size, len(files)))

# 自检：能读回、结构正确
z = zipfile.ZipFile(OUT)
names = z.namelist()
jsons = [n for n in names if n.endswith(".json")]
xamls = [n for n in names if n.endswith(".xaml")]
print("  json: %d   xaml: %d" % (len(jsons), len(xamls)))
missing = []
for j in jsons:
    base = j[:-5]
    if base + ".xaml" not in names:
        missing.append(j)
if missing:
    print("  !! 以下 json 没有对应的 xaml: %s" % missing)
else:
    print("  每个 json 都有对应的 xaml - OK")
