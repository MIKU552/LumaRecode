"""Render the editable vector mark to PNG and a Windows multi-resolution ICO."""
from pathlib import Path
import struct
from PySide6.QtCore import QByteArray, QBuffer, QIODevice, QRectF, Qt
from PySide6.QtGui import QImage, QPainter
from PySide6.QtSvg import QSvgRenderer
from PySide6.QtWidgets import QApplication

root = Path(__file__).resolve().parent
app = QApplication([])
renderer = QSvgRenderer(str(root / 'LumaRecode.svg'))
assert renderer.isValid()

def render(size):
    image = QImage(size, size, QImage.Format.Format_ARGB32_Premultiplied)
    image.fill(Qt.GlobalColor.transparent)
    painter = QPainter(image)
    painter.setRenderHint(QPainter.RenderHint.Antialiasing)
    renderer.render(painter, QRectF(0, 0, size, size))
    painter.end()
    data = QByteArray()
    buffer = QBuffer(data)
    buffer.open(QIODevice.OpenModeFlag.WriteOnly)
    assert image.save(buffer, 'PNG')
    return bytes(data)

(root / 'LumaRecode.png').write_bytes(render(512))
sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
entries = []
payloads = []
offset = 6 + 16 * len(sizes)
for size in sizes:
    data = render(size)
    entries.append(struct.pack('<BBBBHHII', size if size < 256 else 0, size if size < 256 else 0,
                               0, 0, 1, 32, len(data), offset))
    payloads.append(data)
    offset += len(data)
(root.parent / 'App/LumaRecode.ico').write_bytes(struct.pack('<HHH', 0, 1, len(sizes)) + b''.join(entries) + b''.join(payloads))
print('Rendered LumaRecode PNG and 10-resolution ICO')
