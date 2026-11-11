from dataclasses import dataclass, field
from pathlib import Path
import json
import re
import subprocess

NO_WINDOW = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
UNSPECIFIED = {None, '', 'unknown', 'unspecified', 'reserved'}
HDR_KINDS = {'hlg', 'pq'}


@dataclass(frozen=True)
class Tools:
    ffmpeg: str
    ffprobe: str


@dataclass
class Media:
    path: Path
    video: dict
    frames: list
    tags: dict
    kind: str
    profile: str | None
    evidence: str
    duration: float = 0
    streams: list = field(default_factory=list)


def run_probe(tools, arguments, timeout=25):
    result = subprocess.run([tools.ffprobe, '-v', 'error', *arguments], capture_output=True,
                            encoding='utf-8', errors='replace', creationflags=NO_WINDOW, timeout=timeout)
    if result.returncode:
        raise ValueError(result.stderr.strip() or '无法读取视频。')
    return json.loads(result.stdout)


def pb_fields(data):
    def integer(position):
        value = 0
        for shift in range(0, 70, 7):
            if position >= len(data):
                raise ValueError('Incomplete metadata')
            byte = data[position]
            position += 1
            value |= (byte & 127) << shift
            if byte < 128:
                return value, position
        raise ValueError('Invalid metadata')

    position = 0
    result = {}
    while position < len(data):
        key, position = integer(position)
        number, wire = key >> 3, key & 7
        if number == 0:
            raise ValueError('Invalid metadata field')
        if wire == 0:
            value, position = integer(position)
        else:
            if wire == 2:
                length, position = integer(position)
            elif wire in (1, 5):
                length = 8 if wire == 1 else 4
            else:
                raise ValueError('Unsupported metadata field')
            if position + length > len(data):
                raise ValueError('Incomplete metadata field')
            value = data[position:position + length]
            position += length
        result.setdefault(number, value)
    return result


def dji_color(tools, path, streams):
    stream = next((item for item in streams if item.get('codec_tag_string') == 'djmd'), None)
    if stream is None:
        return None
    try:
        packets = run_probe(tools, ['-select_streams', str(stream['index']), '-read_intervals', '%+#1',
                                   '-show_packets', '-show_data', '-of', 'json', str(path)]).get('packets', [])
        if not packets or int(packets[0].get('size', 0)) > 1024 * 1024:
            return None
        lines = packets[0].get('data', '').splitlines()
        hexadecimal = ''.join(line.split(':', 1)[1].split('  ', 1)[0].replace(' ', '')
                              for line in lines if ':' in line)
        message = pb_fields(bytes.fromhex(hexadecimal))
        header = pb_fields(pb_fields(message[1])[1])
        schema = header[1].decode('ascii')
        # Read ColorMode only in layouts with this verified StreamMeta field.
        if schema not in {'dvtm_PP-101.proto', 'dvtm_ac206.proto', 'dvtm_oq101.proto'}:
            return None
        color = pb_fields(pb_fields(message[2])[4]).get(1, 0)
        return schema, color
    except (ValueError, KeyError, TypeError, UnicodeError, subprocess.TimeoutExpired):
        return None


def classify(video, tags, dji=None):
    lowered = {key.lower(): str(value).lower() for key, value in tags.items()}
    if lowered.get('com.xiaomi.record_log') in {'1', 'true', 'yes'}:
        return 'log', 'milog', '小米相机元数据确认 Mi-Log'
    samsung = lowered.get('com.samsung.android.logvideo', '')
    if samsung and samsung not in {'0', 'false', 'no'}:
        return 'log', 'samsung', '三星相机元数据确认 Samsung Log'
    if dji:
        schema, mode = dji
        if mode == 19:
            profile = 'pocket3' if schema == 'dvtm_PP-101.proto' else None
            return 'log', profile, 'DJI 专用元数据确认 D-Log M' if profile else 'DJI 元数据为 D-Log M，请确认机型对应的曲线'
        if mode in {1, 2, 22}:
            return 'log', None, f'DJI Log 标记 ColorMode={mode}，请选择对应曲线'
        if mode == 9:
            return 'hlg', None, 'DJI 专用元数据确认 HLG'
        if mode == 0:
            return 'sdr', None, 'DJI 元数据为普通色彩模式'

    for key, value in lowered.items():
        if any(term in key for term in ('colorgamma', 'color_mode', 'colormode', 'log_profile', 'gamma')):
            compact = re.sub(r'[^a-z0-9]', '', value)
            if compact in {'dlogm', 'djidlogm'}:
                model = ' '.join(lowered.values())
                return 'log', 'pocket3' if 'pocket' in model else None, f'厂商色彩标记 {value}'
            if compact in {'milog', 'xiaomilog', 'samsunglog'}:
                profile = 'samsung' if compact == 'samsunglog' else 'milog'
                return 'log', profile, f'厂商色彩标记 {value}'
            if 'log' in compact:
                return 'log', None, '读到了 Log 标记，具体曲线需要手动确认'
    if any('log' in key and value in {'1', 'true', 'yes', 'log'} for key, value in lowered.items()):
        return 'log', None, '读到了 Log 开关，具体曲线需要手动确认'
    transfer = video.get('color_transfer')
    if transfer == 'arib-std-b67':
        return 'hlg', None, '视频传递曲线为 HLG'
    if transfer == 'smpte2084':
        return 'pq', None, '视频传递曲线为 PQ'
    if transfer in {'bt709', 'smpte170m', 'bt470m', 'bt470bg', 'gamma22', 'gamma28', 'iec61966-2-1', 'bt2020-10', 'bt2020-12'}:
        return 'sdr', None, '视频使用标准 SDR 传递曲线'
    return 'unknown', None, '传递曲线未标明，请确认输入类型或选择 Log 转换'


def inspect_media(tools, path):
    path = Path(path).resolve()
    data = run_probe(tools, ['-show_streams', '-show_format', '-of', 'json', str(path)])
    streams = data.get('streams', [])
    video = next((item for item in streams if item.get('codec_type') == 'video'
                  and not item.get('disposition', {}).get('attached_pic')), None)
    if video is None:
        raise ValueError('文件中没有可用的视频轨。')
    frame_data = run_probe(tools, ['-select_streams', str(video['index']), '-read_intervals', '%+#1',
                                  '-show_frames', '-of', 'json', str(path)])
    frames = frame_data.get('frames', [])
    for name in ('color_transfer', 'color_primaries', 'color_space', 'color_range'):
        if video.get(name) in UNSPECIFIED:
            value = next((frame.get(name) for frame in frames if frame.get(name) not in UNSPECIFIED), None)
            if value:
                video[name] = value
    tags = {**data.get('format', {}).get('tags', {}), **video.get('tags', {})}
    dji = dji_color(tools, path, streams)
    kind, profile, evidence = classify(video, tags, dji)
    duration = float(data.get('format', {}).get('duration') or video.get('duration') or 0)
    return Media(path, video, frames, tags, kind, profile, evidence, duration, streams)
