from pathlib import Path
import ctypes
import os
import shutil
import subprocess
import sys
import threading
import traceback

from PySide6.QtCore import QObject, QPoint, Qt, QSettings, QTimer, QUrl, Signal
from PySide6.QtGui import QColor, QDesktopServices, QFont, QIcon, QPainter, QPixmap, QPolygon
from PySide6.QtWidgets import (QApplication, QButtonGroup, QCheckBox, QComboBox, QDialog,
    QFileDialog, QFormLayout, QFrame, QGridLayout, QHBoxLayout, QLabel, QLineEdit,
    QMainWindow, QMessageBox, QPlainTextEdit, QProgressBar, QPushButton, QScrollArea,
    QSizePolicy, QVBoxLayout, QWidget)

from engine import ASSETS, CONFIG, PROFILES, Cancelled, Job, Options, validate
from media import HDR_KINDS, NO_WINDOW, Tools, inspect_media

KIND_NAMES = {'sdr': 'SDR', 'hlg': 'HLG HDR', 'pq': 'PQ HDR', 'log': 'Log', 'unknown': '待确认'}
GROUP_NAMES = {'none': '不套用 · 保留 / 标准化源色彩', 'log-sdr': 'Log → SDR',
               'log-hdr': 'Log → HDR（HLG）', 'hdr-sdr': 'HDR → SDR'}


def application_icon():
    pixmap = QPixmap(64, 64)
    pixmap.fill(Qt.GlobalColor.transparent)
    painter = QPainter(pixmap)
    painter.setRenderHint(QPainter.RenderHint.Antialiasing)
    painter.setPen(Qt.PenStyle.NoPen)
    painter.setBrush(QColor('#0067c0'))
    painter.drawRoundedRect(1, 1, 62, 62, 16, 16)
    painter.setBrush(QColor('white'))
    painter.drawRoundedRect(13, 19, 27, 26, 5, 5)
    painter.drawPolygon(QPolygon([QPoint(x, y) for x, y in [(43, 26), (53, 20), (53, 44), (43, 38)]]))
    painter.end()
    return QIcon(pixmap)


class Segmented(QWidget):
    changed = Signal(str)

    def __init__(self, values, initial, parent=None):
        super().__init__(parent)
        self.buttons = {}
        self.group = QButtonGroup(self)
        layout = QHBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(4)
        for key, label in values:
            button = QPushButton(label)
            button.setObjectName('segment')
            button.setCheckable(True)
            button.setMinimumHeight(36)
            self.group.addButton(button)
            self.buttons[key] = button
            layout.addWidget(button)
            button.clicked.connect(lambda checked, value=key: self.changed.emit(value))
        self.set_value(initial)

    def value(self):
        return next(key for key, button in self.buttons.items() if button.isChecked())

    def set_value(self, value):
        self.buttons[value].setChecked(True)


class Card(QFrame):
    def __init__(self, title):
        super().__init__()
        self.setObjectName('card')
        self.body = QVBoxLayout(self)
        self.body.setContentsMargins(20, 17, 20, 18)
        self.body.setSpacing(13)
        heading = QLabel(title)
        heading.setObjectName('cardTitle')
        self.body.addWidget(heading)


class ToolDialog(QDialog):
    def __init__(self, tools, parent):
        super().__init__(parent)
        self.setWindowTitle('FFmpeg 工具路径')
        self.resize(660, 260)
        self.tools = tools
        self.fields = {}
        page = QVBoxLayout(self)
        page.setContentsMargins(24, 20, 24, 20)
        text = QLabel('优先使用 PATH 中的工具。也可以选择自己的 FFmpeg 和 ffprobe，路径会记在这台电脑上。')
        text.setWordWrap(True)
        page.addWidget(text)
        form = QFormLayout()
        for name in ('ffmpeg', 'ffprobe'):
            row = QHBoxLayout()
            field = QLineEdit(getattr(tools, name, '') if tools else shutil.which(name) or '')
            field.setMinimumWidth(440)
            browse = QPushButton('浏览…')
            browse.clicked.connect(lambda checked, selected=name: self.browse(selected))
            row.addWidget(field, 1)
            row.addWidget(browse)
            self.fields[name] = field
            form.addRow(name, row)
        page.addLayout(form)
        self.message = QLabel('')
        self.message.setWordWrap(True)
        self.message.setObjectName('warning')
        page.addWidget(self.message)
        actions = QHBoxLayout()
        actions.addStretch()
        cancel = QPushButton('取消')
        cancel.clicked.connect(self.reject)
        save = QPushButton('检测并保存')
        save.setObjectName('primary')
        save.clicked.connect(self.save)
        actions.addWidget(cancel)
        actions.addWidget(save)
        page.addLayout(actions)

    def browse(self, name):
        path, _ = QFileDialog.getOpenFileName(self, f'选择 {name}.exe', '', '可执行文件 (*.exe)')
        if path:
            self.fields[name].setText(path)
            peer = 'ffprobe' if name == 'ffmpeg' else 'ffmpeg'
            neighbor = Path(path).with_name(peer + '.exe')
            if neighbor.is_file() and not self.fields[peer].text():
                self.fields[peer].setText(str(neighbor))

    def save(self):
        paths = {}
        try:
            for name, field in self.fields.items():
                value = field.text().strip().strip('"')
                path = Path(value)
                if not value or not path.is_file():
                    raise ValueError(f'请选择 {name}.exe。')
                result = subprocess.run([str(path), '-version'], capture_output=True, encoding='utf-8',
                                        errors='replace', creationflags=NO_WINDOW, timeout=10)
                if result.returncode or name + ' version' not in result.stdout.lower():
                    raise ValueError(f'所选文件未通过 {name} 版本检测。')
                paths[name] = str(path.resolve())
        except (ValueError, OSError, subprocess.TimeoutExpired) as error:
            self.message.setText(str(error))
            return
        self.tools = Tools(**paths)
        self.accept()


class Bridge(QObject):
    event = Signal(str, object)


class ConverterWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle('视频转换')
        self.setWindowIcon(application_icon())
        self.resize(1080, 850)
        self.setMinimumSize(900, 720)
        self.setAcceptDrops(True)
        self.settings = QSettings('LocalVideoTools', 'VideoConverter')
        self.tools = self.find_tools()
        self.media = None
        self.generation = 0
        self.inspection_pending = False
        self.job = None
        self.closing = False
        self.updating = False
        self.auto_output = ''
        self.output_path = None
        self.bridge = Bridge()
        self.bridge.event.connect(self.receive)
        self.build_ui()
        self.refresh()
        QTimer.singleShot(0, self.initialize)

    def find_tools(self):
        paths = {}
        for name in ('ffmpeg', 'ffprobe'):
            saved = str(self.settings.value(name, ''))
            paths[name] = saved if saved and Path(saved).is_file() else shutil.which(name) or ''
        return Tools(**paths) if all(paths.values()) else None

    def initialize(self):
        try:
            corners = ctypes.c_int(2)
            ctypes.windll.dwmapi.DwmSetWindowAttribute(int(self.winId()), 33, ctypes.byref(corners), 4)
            backdrop = ctypes.c_int(2)
            ctypes.windll.dwmapi.DwmSetWindowAttribute(int(self.winId()), 38, ctypes.byref(backdrop), 4)
        except (AttributeError, OSError):
            pass
        if not self.tools:
            self.configure_tools()

    def build_ui(self):
        page = QWidget()
        page.setObjectName('page')
        layout = QVBoxLayout(page)
        layout.setContentsMargins(28, 22, 28, 20)
        layout.setSpacing(16)
        header = QHBoxLayout()
        heading = QVBoxLayout()
        title = QLabel('视频转换')
        title.setObjectName('title')
        heading.addWidget(title)
        subtitle = QLabel('选择输出，再选择适合这条素材的压缩方式。')
        subtitle.setObjectName('muted')
        heading.addWidget(subtitle)
        header.addLayout(heading, 1)
        self.tool_button = QPushButton('工具路径')
        self.tool_button.clicked.connect(self.configure_tools)
        about = QPushButton('关于')
        about.clicked.connect(self.about)
        header.addWidget(self.tool_button)
        header.addWidget(about)
        layout.addLayout(header)

        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.Shape.NoFrame)
        content = QWidget()
        content.setObjectName('content')
        columns = QGridLayout(content)
        columns.setContentsMargins(0, 0, 4, 0)
        columns.setHorizontalSpacing(16)
        columns.setVerticalSpacing(16)
        columns.setColumnStretch(0, 1)
        columns.setColumnStretch(1, 1)

        source_card = Card('01   视频素材')
        self.input_path = QLineEdit()
        self.input_path.setPlaceholderText('拖入视频，或点击选择文件')
        self.input_path.editingFinished.connect(self.inspect_input)
        self.input_button = QPushButton('选择文件')
        self.input_button.clicked.connect(self.choose_input)
        file_row = QHBoxLayout()
        file_row.addWidget(self.input_path, 1)
        file_row.addWidget(self.input_button)
        source_card.body.addLayout(file_row)
        self.source_summary = QLabel('等待选择视频')
        self.source_summary.setObjectName('sourceSummary')
        self.source_summary.setWordWrap(True)
        source_card.body.addWidget(self.source_summary)
        self.source_evidence = QLabel('支持 SDR、HLG / PQ，以及 Mi-Log、D-Log M、Samsung Log。')
        self.source_evidence.setObjectName('muted')
        self.source_evidence.setWordWrap(True)
        source_card.body.addWidget(self.source_evidence)
        input_form = QFormLayout()
        self.input_kind = QComboBox()
        for value, label in [('auto', '自动识别'), ('sdr', '手动指定 · SDR'), ('hlg', '手动指定 · HLG HDR'),
                             ('pq', '手动指定 · PQ HDR'), ('log', '手动指定 · Log')]:
            self.input_kind.addItem(label, value)
        self.input_kind.currentIndexChanged.connect(self.input_changed)
        input_form.addRow('输入解释', self.input_kind)
        source_card.body.addLayout(input_form)
        source_card.body.addStretch()
        columns.addWidget(source_card, 0, 0)

        output_card = Card('02   输出目标')
        self.output_range = Segmented([('sdr', 'SDR'), ('hdr', 'HDR')], 'sdr')
        self.output_range.changed.connect(self.refresh)
        output_card.body.addWidget(self.output_range)
        self.target_summary = QLabel('SDR · Rec.709')
        self.target_summary.setObjectName('sourceSummary')
        self.target_summary.setWordWrap(True)
        output_card.body.addWidget(self.target_summary)
        self.target_note = QLabel('选择视频后，按原片动态范围自动设置。')
        self.target_note.setObjectName('muted')
        self.target_note.setWordWrap(True)
        output_card.body.addWidget(self.target_note)
        sampling_form = QFormLayout()
        self.sampling = QComboBox()
        self.sampling.addItem('4:2:0 · 播放兼容优先', '420')
        self.sampling.addItem('4:2:2 · 软件归档', '422')
        self.sampling.currentIndexChanged.connect(self.refresh)
        sampling_form.addRow('色度采样', self.sampling)
        output_card.body.addLayout(sampling_form)
        columns.addWidget(output_card, 0, 1)

        encode_card = Card('03   压缩配置')
        self.purpose = Segmented([('share', '快速分享 · 硬件'), ('archive', '软件归档')], 'share')
        self.purpose.changed.connect(self.refresh)
        encode_card.body.addWidget(self.purpose)
        self.codec = Segmented([('hevc', 'HEVC'), ('av1', 'AV1'), ('avc', 'H.264 / AVC')], 'hevc')
        self.codec.changed.connect(self.refresh)
        encode_card.body.addWidget(self.codec)
        form = QFormLayout()
        self.variant = QComboBox()
        self.variant.addItem('标准实测档', 'standard')
        self.variant.addItem('AV1 更快备用档', 'fast')
        self.variant.currentIndexChanged.connect(self.refresh)
        form.addRow('质量档', self.variant)
        self.audio = QComboBox()
        for value, label in [('auto', '按编码档默认'), ('aac', 'AAC · 256 kbps'), ('opus', 'Opus · 192 kbps'), ('copy', '复制原音轨')]:
            self.audio.addItem(label, value)
        form.addRow('音轨', self.audio)
        encode_card.body.addLayout(form)
        self.preset_summary = QLabel('')
        self.preset_summary.setObjectName('muted')
        self.preset_summary.setWordWrap(True)
        encode_card.body.addWidget(self.preset_summary)
        self.cpu_decode = QCheckBox('使用软件解码')
        self.cpu_decode.setToolTip('套 LUT 或输入 4:2:2 时自动使用软件解码。此选项保留 QSV 硬件编码。')
        encode_card.body.addWidget(self.cpu_decode)
        columns.addWidget(encode_card, 1, 0)

        color_card = Card('04   LUT / 色调映射')
        self.transform = QComboBox()
        for key, label in GROUP_NAMES.items():
            self.transform.addItem(label, key)
        self.transform.currentIndexChanged.connect(self.transform_changed)
        color_card.body.addWidget(self.transform)
        self.profile = QComboBox()
        self.profile.addItem('请选择 Log 曲线…', None)
        for key, value in PROFILES.items():
            self.profile.addItem(value[0], key)
        self.profile.currentIndexChanged.connect(self.refresh)
        color_card.body.addWidget(self.profile)
        self.transform_note = QLabel('')
        self.transform_note.setObjectName('muted')
        self.transform_note.setWordWrap(True)
        color_card.body.addWidget(self.transform_note)
        self.notice = QLabel('')
        self.notice.setObjectName('warning')
        self.notice.setWordWrap(True)
        color_card.body.addWidget(self.notice)
        color_card.body.addStretch()
        columns.addWidget(color_card, 1, 1)
        self.settings_content = content
        scroll.setWidget(content)
        layout.addWidget(scroll, 1)

        save_card = QFrame()
        save_card.setObjectName('card')
        save_layout = QVBoxLayout(save_card)
        save_layout.setContentsMargins(18, 14, 18, 14)
        destination_row = QHBoxLayout()
        destination_row.addWidget(QLabel('保存到'))
        self.destination = QLineEdit()
        self.destination.setPlaceholderText('自动生成输出文件名')
        self.save_button = QPushButton('更改…')
        self.save_button.clicked.connect(self.choose_output)
        destination_row.addWidget(self.destination, 1)
        destination_row.addWidget(self.save_button)
        save_layout.addLayout(destination_row)
        self.progress = QProgressBar()
        self.progress.setTextVisible(False)
        self.progress.setRange(0, 1000)
        self.progress.setFixedHeight(6)
        save_layout.addWidget(self.progress)
        actions = QHBoxLayout()
        self.status = QLabel('选择视频后即可开始')
        self.status.setObjectName('muted')
        actions.addWidget(self.status, 1)
        self.log_button = QPushButton('查看日志')
        self.log_button.setCheckable(True)
        self.log_button.toggled.connect(self.toggle_log)
        self.open_button = QPushButton('打开目录')
        self.open_button.setEnabled(False)
        self.open_button.clicked.connect(self.open_folder)
        self.cancel_button = QPushButton('取消')
        self.cancel_button.setEnabled(False)
        self.cancel_button.clicked.connect(self.cancel)
        self.start_button = QPushButton('开始转换')
        self.start_button.setObjectName('primary')
        self.start_button.setMinimumWidth(120)
        self.start_button.clicked.connect(self.start)
        for button in (self.log_button, self.open_button, self.cancel_button, self.start_button):
            actions.addWidget(button)
        save_layout.addLayout(actions)
        layout.addWidget(save_card)
        self.log = QPlainTextEdit()
        self.log.setReadOnly(True)
        self.log.setMaximumBlockCount(350)
        self.log.setFixedHeight(145)
        self.log.setVisible(False)
        layout.addWidget(self.log)
        self.setCentralWidget(page)

    def options(self):
        return Options(purpose=self.purpose.value(), codec=self.codec.value(), output=self.output_range.value(),
                       transform=self.transform.currentData(), profile=self.profile.currentData(),
                       sampling=self.sampling.currentData(), variant=self.variant.currentData(),
                       audio=self.audio.currentData(), source_kind=self.input_kind.currentData(),
                       cpu_decode=self.cpu_decode.isChecked())

    def current_kind(self):
        if self.input_kind.currentData() != 'auto':
            return self.input_kind.currentData()
        return self.media.kind if self.media else 'unknown'

    def input_changed(self):
        if self.updating:
            return
        kind = self.current_kind()
        if kind == 'sdr':
            self.output_range.set_value('sdr')
        self.refresh()

    def transform_changed(self):
        if self.updating:
            return
        if self.transform.currentData() in {'log-sdr', 'log-hdr'} and self.current_kind() == 'sdr':
            self.input_kind.setCurrentIndex(self.input_kind.findData('log'))
        self.refresh()

    def refresh(self, *_):
        if self.updating:
            return
        self.updating = True
        kind = self.current_kind()
        hdr = self.output_range.value() == 'hdr'
        self.output_range.buttons['hdr'].setEnabled(kind != 'sdr')
        if kind == 'sdr' and hdr:
            self.output_range.set_value('sdr')
            hdr = False
        self.codec.buttons['avc'].setEnabled(not hdr)
        if hdr and self.codec.value() == 'avc':
            self.codec.set_value('hevc')
        purpose, codec = self.purpose.value(), self.codec.value()
        allow_422 = purpose == 'archive' and codec != 'av1'
        self.sampling.model().item(1).setEnabled(allow_422)
        if not allow_422:
            self.sampling.setCurrentIndex(0)
        allow_fast = purpose == 'archive' and codec == 'av1'
        self.variant.model().item(1).setEnabled(allow_fast)
        self.variant.setEnabled(allow_fast)
        if not allow_fast:
            self.variant.setCurrentIndex(0)
        if kind in HDR_KINDS:
            allowed = ['none'] if hdr else ['hdr-sdr']
        elif kind == 'log':
            allowed = ['log-hdr'] if hdr else ['log-sdr']
        else:
            allowed = ['log-hdr'] if hdr else ['none', 'log-sdr']
        current = self.transform.currentData()
        for index in range(self.transform.count()):
            self.transform.model().item(index).setEnabled(self.transform.itemData(index) in allowed)
        if current not in allowed:
            self.transform.setCurrentIndex(self.transform.findData(allowed[0]))
        log_transform = self.transform.currentData() in {'log-hdr', 'log-sdr'}
        self.profile.setEnabled(log_transform)
        options = self.options()
        sampling = '4:2:0' if options.sampling == '420' else '4:2:2'
        if hdr:
            transfer = 'HLG' if log_transform or kind == 'hlg' else 'PQ' if kind == 'pq' else 'HLG / PQ'
            self.target_summary.setText(f'HDR · BT.2020 {transfer} · {options.depth} 位')
            self.target_note.setText(f'输出 {sampling}。保留高光亮度信息，使用 HEVC 或 AV1。')
        else:
            self.target_summary.setText(f'SDR · Rec.709 · {options.depth} 位')
            note = '快速分享使用 SDR8 实测档。' if purpose == 'share' else '软件归档保留对应预设的位深。'
            self.target_note.setText(f'输出 {sampling}。{note}')
        config = CONFIG['archive_fast_av1'] if options.variant == 'fast' else CONFIG[purpose][codec]
        quality = f'TU{config["preset"]} · ICQ {config["quality"]}' if purpose == 'share' else f'{config["preset"]} · CRF {config["quality"]}'
        self.preset_summary.setText(f'{config["encoder"]} · {quality}\n{options.depth} 位 {sampling} · {options.suffix[1:].upper()}')
        notes = {
            'none': '按源色彩解释视频，再输出所选的位深和采样格式。',
            'log-hdr': '选对应相机的曲线，转换到 10 位 BT.2020 HLG。',
            'log-sdr': '先解码对应 Log，再映射到 SDR。使用同一条滤镜链完成。',
            'hdr-sdr': '按原片 HLG / PQ 做 BT.2390 色调映射，输出 Rec.709。',
        }
        self.transform_note.setText(notes[self.transform.currentData()])
        problem = ''
        if not self.tools:
            problem = '未找到 FFmpeg / ffprobe，请选择工具路径。'
        elif self.media:
            try:
                validate(options, self.media)
            except ValueError as error:
                problem = str(error)
        self.notice.setText(problem)
        self.notice.setVisible(bool(problem))
        self.start_button.setEnabled(bool(self.media and self.tools and not self.job and not self.inspection_pending and not problem))
        self.update_output_name(options)
        self.updating = False

    def update_output_name(self, options):
        if not self.media:
            return
        if self.destination.text() in ('', self.auto_output):
            name = f'{self.media.path.stem}_{options.output.upper()}_{options.codec}_{options.purpose}{options.suffix}'
            self.auto_output = str(self.media.path.with_name(name))
            self.destination.setText(self.auto_output)

    def choose_input(self):
        path, _ = QFileDialog.getOpenFileName(self, '选择视频', '', '视频 (*.mp4 *.mov *.mkv *.m4v *.webm *.avi *.mts);;所有文件 (*)')
        if path:
            self.input_path.setText(path)
            self.inspect_input()

    def inspect_input(self):
        if self.job:
            return
        value = self.input_path.text().strip().strip('"')
        if not value:
            return
        if not self.tools:
            self.configure_tools()
            return
        path = Path(value)
        if not path.is_file():
            QMessageBox.warning(self, '无法读取', '请选择一个有效的视频文件。')
            return
        if self.media and self.media.path == path.resolve():
            return
        self.generation += 1
        token = self.generation
        self.inspection_pending = True
        self.media = None
        self.source_summary.setText('正在识别视频…')
        self.source_evidence.setText('读取色彩标签和相机元数据')
        self.status.setText('正在分析素材')
        self.refresh()
        tools = self.tools

        def task():
            try:
                self.bridge.event.emit('media', (token, inspect_media(tools, path)))
            except Exception as error:
                self.bridge.event.emit('probe-error', (token, str(error)))

        threading.Thread(target=task, daemon=True).start()

    def apply_media(self, media):
        self.media = media
        self.input_kind.setCurrentIndex(0)
        self.purpose.set_value('share')
        self.codec.set_value('hevc')
        self.variant.setCurrentIndex(0)
        self.sampling.setCurrentIndex(0)
        self.audio.setCurrentIndex(0)
        self.output_range.set_value('hdr' if media.kind in HDR_KINDS or media.kind == 'log' else 'sdr')
        self.profile.setCurrentIndex(max(0, self.profile.findData(media.profile)))
        self.destination.clear()
        pixels = media.video.get('pix_fmt', '')
        sampling = '4:2:2' if '422' in pixels else '4:2:0' if '420' in pixels or pixels == 'p010le' else pixels
        name = PROFILES[media.profile][0] if media.profile else KIND_NAMES[media.kind]
        self.source_summary.setText(f'{name}  ·  {media.video["width"]} × {media.video["height"]}  ·  {sampling}')
        self.source_evidence.setText(media.evidence)
        self.source_evidence.setToolTip(media.evidence)
        self.status.setText(f'已识别 · {media.duration:.1f} 秒')
        self.refresh()
        if media.kind == 'log' and not media.profile:
            QMessageBox.information(self, '请确认 Log 曲线', '读到了 Log 标记，但不能确认具体曲线。请在 LUT 分组里选择对应相机。')

    def choose_output(self):
        options = self.options()
        value = Path(self.destination.text()) if self.destination.text() else Path.home() / ('video' + options.suffix)
        path, _ = QFileDialog.getSaveFileName(self, '输出文件', str(value), f'{options.suffix[1:].upper()} (*{options.suffix})')
        if path:
            self.destination.setText(path if Path(path).suffix else path + options.suffix)

    def configure_tools(self):
        if self.job:
            return
        dialog = ToolDialog(self.tools, self)
        if dialog.exec() == QDialog.DialogCode.Accepted:
            self.tools = dialog.tools
            for name in ('ffmpeg', 'ffprobe'):
                self.settings.setValue(name, getattr(self.tools, name))
            self.status.setText('FFmpeg 和 ffprobe 已就绪')
            self.refresh()
            if self.input_path.text() and not self.media:
                self.inspect_input()

    def start(self):
        if not self.media or not self.tools or self.job:
            return
        options = self.options()
        text = self.destination.text().strip().strip('"')
        if not text:
            QMessageBox.warning(self, '输出位置', '请选择输出文件。')
            return
        output = Path(text).resolve()
        try:
            validate(options, self.media)
            if output.exists():
                raise ValueError('输出文件已存在，请更换文件名。')
            if not output.parent.is_dir() or output.suffix.lower() != options.suffix:
                raise ValueError(f'请选择已有的输出目录，后缀应为 {options.suffix}。')
        except ValueError as error:
            QMessageBox.warning(self, '请调整选项', str(error))
            return
        self.job = Job()
        job, media, tools = self.job, self.media, self.tools
        self.output_path = output
        self.progress.setValue(0)
        self.status.setText('正在启动 FFmpeg…')
        self.log.clear()
        self.set_running(True)

        def worker():
            try:
                result = job.run(tools, media, options, output, emit=self.bridge.event.emit)
                self.bridge.event.emit('done', result)
            except Cancelled:
                self.bridge.event.emit('cancelled', None)
            except Exception as error:
                self.bridge.event.emit('error', str(error))

        threading.Thread(target=worker, daemon=True).start()

    def set_running(self, running):
        for control in (self.settings_content, self.tool_button, self.destination, self.save_button):
            control.setEnabled(not running)
        self.cancel_button.setEnabled(running)
        self.open_button.setEnabled(False)
        self.start_button.setEnabled(not running and bool(self.media))

    def receive(self, event, data):
        if event in {'media', 'probe-error'}:
            token, payload = data
            if token != self.generation:
                return
            self.inspection_pending = False
            if event == 'media':
                self.apply_media(payload)
            else:
                self.source_summary.setText('无法识别该文件')
                self.source_evidence.setText(payload)
                self.status.setText('读取失败')
                self.refresh()
            return
        if event == 'log':
            self.log.appendPlainText(data)
        elif event == 'status':
            self.status.setText(data)
        elif event == 'progress':
            try:
                elapsed = float(data.get('out_time_us', 0)) / 1_000_000
                duration = float(data.get('duration') or 0)
                if duration:
                    self.progress.setValue(min(990, max(0, int(1000 * elapsed / duration))))
            except (ValueError, TypeError):
                pass
            if self.cancel_button.isEnabled():
                self.status.setText(f'转换中 · {data.get("frame", "0")} 帧 · {data.get("speed", "—")}')
        else:
            self.job = None
            self.set_running(False)
            if self.closing:
                self.close()
                return
            if event == 'done':
                self.progress.setValue(1000)
                self.status.setText('转换完成 · 已验证色彩和像素格式')
                self.open_button.setEnabled(True)
                self.log.appendPlainText(f'已保存 {self.output_path}')
            elif event == 'cancelled':
                self.progress.setValue(0)
                self.status.setText('已取消')
            elif event == 'error':
                self.status.setText('转换失败')
                self.log.appendPlainText(data)
                self.log_button.setChecked(True)
                QMessageBox.warning(self, '转换失败', data)
            self.refresh()

    def cancel(self):
        if self.job:
            self.job.cancel()
            self.cancel_button.setEnabled(False)
            self.status.setText('正在取消…')

    def open_folder(self):
        if self.output_path:
            QDesktopServices.openUrl(QUrl.fromLocalFile(str(self.output_path.parent)))

    def toggle_log(self, visible):
        self.log.setVisible(visible)
        self.log_button.setText('收起日志' if visible else '查看日志')

    def about(self):
        QMessageBox.about(self, '关于视频转换',
            '视频转换 2.0\n\nWindows 11 Fluent 风格，使用 PySide6 / Qt。\n'
            '压缩参数来自你的实测配置。\nFFmpeg 和 ffprobe 从 PATH 或自选路径运行。\n\n'
            'Mi-Log、Samsung Log 转换基于公开曲线。\nPocket 3 HLG LUT 来自 OpenOSV v0.6.4。\n'
            'Log → SDR 在同一条滤镜链里接 BT.2390 色调映射。\n\n'
            '第三方许可随程序资源附带。')

    def dragEnterEvent(self, event):
        if not self.job and event.mimeData().hasUrls() and len(event.mimeData().urls()) == 1:
            event.acceptProposedAction()

    def dropEvent(self, event):
        urls = event.mimeData().urls()
        if not self.job and len(urls) == 1 and urls[0].isLocalFile():
            self.input_path.setText(urls[0].toLocalFile())
            self.inspect_input()
            event.acceptProposedAction()

    def closeEvent(self, event):
        if self.job:
            if QMessageBox.question(self, '退出', '是否取消当前转换并退出？') == QMessageBox.StandardButton.Yes:
                self.closing = True
                self.cancel()
            event.ignore()
        else:
            event.accept()


STYLE = '''
QWidget { font-family: "Segoe UI", "Microsoft YaHei UI"; font-size: 13px; color: #172131; }
QWidget#page, QWidget#content { background: #f3f5f8; }
QLabel { background: transparent; }
QLabel#title { font-size: 29px; font-weight: 650; color: #15243b; }
QLabel#cardTitle { font-size: 15px; font-weight: 650; }
QLabel#sourceSummary { font-size: 15px; font-weight: 600; }
QLabel#muted { color: #637184; line-height: 1.4; }
QLabel#warning { color: #9a5a00; background: #fff3d5; border-radius: 7px; padding: 9px; }
QFrame#card { background: #ffffff; border: 1px solid #e0e5ed; border-radius: 12px; }
QLineEdit, QComboBox { background: #ffffff; border: 1px solid #cfd5df; border-bottom: 1px solid #8c97a8;
    border-radius: 5px; padding: 7px 9px; min-height: 19px; selection-background-color: #d9eaff; }
QLineEdit:focus, QComboBox:focus { border-bottom: 2px solid #0067c0; }
QComboBox::drop-down { border: none; width: 24px; }
QComboBox::down-arrow { image: url("CHEVRON_URI"); width: 12px; height: 12px; }
QComboBox QAbstractItemView { border: 1px solid #d7dfeb; padding: 5px; background: white; selection-background-color: #e6f1ff; selection-color: #0d427b; }
QPushButton { background: #ffffff; border: 1px solid #d3dbe5; border-radius: 6px; padding: 8px 13px; min-height: 18px; }
QPushButton:hover { background: #f1f6fd; border-color: #b8cce5; }
QPushButton:pressed { background: #e5edf8; }
QPushButton:disabled { color: #a5acb7; background: #f5f6f8; border-color: #e4e7ed; }
QPushButton#primary { background: #0067c0; color: white; border-color: #0067c0; font-weight: 600; }
QPushButton#primary:hover { background: #005eaf; }
QPushButton#primary:disabled { background: #c3d2e3; border-color: #c3d2e3; color: white; }
QPushButton#segment { background: #f6f8fb; border: 1px solid #e1e6ee; padding: 5px 9px; }
QPushButton#segment:checked { color: #005cae; background: #e9f3ff; border: 1px solid #7eb2ed; font-weight: 600; }
QPushButton#segment:disabled { color: #adb5c1; background: #f6f7f9; border-color: #edf0f4; }
QProgressBar { border: none; border-radius: 3px; background: #e6ebf2; }
QProgressBar::chunk { background: #1476ce; border-radius: 3px; }
QCheckBox { spacing: 7px; color: #596a80; }
QScrollArea { background: #f3f5f8; border: none; }
QScrollBar:vertical { background: transparent; width: 8px; margin: 0px; }
QScrollBar::handle:vertical { background: #c4cbd6; border-radius: 4px; min-height: 30px; }
QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical { height: 0px; }
QPlainTextEdit { background: #ffffff; border: 1px solid #dce3ed; border-radius: 9px; padding: 9px; font-family: Consolas; font-size: 12px; }
QDialog { background: #f3f5f8; }
'''.replace('CHEVRON_URI', (ASSETS / 'chevron.png').as_posix())


def main():
    try:
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID('LocalVideoTools.VideoConverter')
    except AttributeError:
        pass
    app = QApplication(sys.argv)
    app.setApplicationName('视频转换')
    app.setStyle('Fusion')
    app.setStyleSheet(STYLE)
    def exception_hook(kind, value, trace):
        diagnostic = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'VideoConverter' / 'error.log'
        try:
            diagnostic.parent.mkdir(parents=True, exist_ok=True)
            diagnostic.write_text(''.join(traceback.format_exception(kind, value, trace)), encoding='utf-8')
            message = f'{value}\n\n诊断信息已保存到 {diagnostic}'
        except OSError:
            message = str(value)
        QMessageBox.critical(None, '程序错误', message)
    sys.excepthook = exception_hook
    window = ConverterWindow()
    available = app.primaryScreen().availableGeometry()
    window.resize(min(1080, available.width() - 50), min(850, available.height() - 65))
    window.show()
    if len(sys.argv) > 1:
        window.input_path.setText(sys.argv[1])
        QTimer.singleShot(50, window.inspect_input)
    sys.exit(app.exec())


if __name__ == '__main__':
    main()
