# 图标资源 / Icon assets

LumaRecode 图标由前后叠放的画幅与向右箭头组成，使用青绿至蓝紫渐变。

| 文件 | 规格 | 用途 |
| --- | --- | --- |
| [LumaRecode.svg](LumaRecode.svg) | 256×256 设计坐标 | 矢量源文件 |
| [LumaRecode.png](LumaRecode.png) | 512×512，透明背景 | 标题栏和关于窗口 |
| [LumaRecode.ico](../App/LumaRecode.ico) | 16、20、24、32、40、48、64、96、128、256 px | EXE 和任务栏 |

在仓库根目录重新生成 PNG 和 ICO：

```powershell
python -m pip install -r Brand/requirements.txt
python Brand/render_icon.py
```

## English

The mark combines overlapping frames and a right arrow with a teal-to-violet gradient. Run the commands above from the repository root to render the PNG and multi-resolution Windows icon.
