# 图标资源 / Icon assets

[简体中文](#简体中文) · [English](#english)

## 简体中文

### 资源文件

LumaRecode 图标由前后叠放的画幅与向右箭头组成，使用青绿至蓝紫渐变。

| 文件 | 规格 | 用途 |
| --- | --- | --- |
| [LumaRecode.svg](LumaRecode.svg) | 256×256 设计坐标 | 矢量源文件 |
| [LumaRecode.png](LumaRecode.png) | 512×512，透明背景 | 标题栏和关于窗口 |
| [LumaRecode.ico](../App/LumaRecode.ico) | 16、20、24、32、40、48、64、96、128、256 px | EXE 和任务栏 |

### 生成图标

在仓库根目录重新生成 PNG 和 ICO：

```powershell
python -m pip install -r Brand/requirements.txt
python Brand/render_icon.py
```

## English

### Asset files

The LumaRecode mark combines overlapping frames and a right arrow with a teal-to-violet gradient.

| File | Specification | Use |
| --- | --- | --- |
| [LumaRecode.svg](LumaRecode.svg) | 256×256 design coordinates | Vector source |
| [LumaRecode.png](LumaRecode.png) | 512×512, transparent background | Title bar and About |
| [LumaRecode.ico](../App/LumaRecode.ico) | 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 px | EXE and taskbar |

### Generate icons

Run from the repository root to regenerate PNG and ICO:

```powershell
python -m pip install -r Brand/requirements.txt
python Brand/render_icon.py
```
