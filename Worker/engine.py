from dataclasses import dataclass, replace
from pathlib import Path
import json
import re
import subprocess
import sys
import tempfile
import threading

from media import HDR_KINDS, NO_WINDOW, UNSPECIFIED, inspect_media
from metadata_helpers import compare_static_metadata, hdr_metadata, nominal_fps, side_data
from output_policy import audio_streams, flac_tracks, is_rgb, source_range, source_sampling

ASSETS = Path(getattr(sys, '_MEIPASS', Path(__file__).resolve().parent)) / 'assets'
CONFIG = json.loads((ASSETS / 'recommended-presets.json').read_text(encoding='utf-8'))
PROFILES = {
    'milog': ('小米 Mi-Log', 'REDMI_K90_MiLog_to_Rec2100_HLG.cube', '2020_ncl', 'full'),
    'pocket3': ('Pocket 3 D-Log M', 'DJI_Osmo_DLogM_to_Rec2100_HLG_BT2408_Neutral.cube', '709', 'limited'),
    'samsung': ('Samsung Log', 'Samsung_Log_to_Rec2100_HLG.cube', '2020_ncl', 'limited'),
}
REC709 = dict(color_primaries='bt709', color_transfer='bt709', color_space='bt709', color_range='tv')
HLG = dict(color_primaries='bt2020', color_transfer='arib-std-b67', color_space='bt2020nc', color_range='tv')
COLOR_FLAGS = {'color_primaries': '-color_primaries', 'color_transfer': '-color_trc',
               'color_space': '-colorspace', 'color_range': '-color_range'}


@dataclass(frozen=True)
class Options:
    purpose: str = 'share'
    codec: str = 'hevc'
    output: str = 'sdr'
    transform: str = 'none'
    profile: str | None = None
    sampling: str = 'auto'
    variant: str = 'standard'
    audio: str = 'auto'
    source_kind: str = 'auto'
    cpu_decode: bool = False
    container: str = 'auto'
    color_range: str = 'auto'

    @property
    def suffix(self):
        chosen = ('mkv' if self.purpose == 'archive' or self.audio == 'flac' else 'mp4') if self.container == 'auto' else self.container
        return '.' + chosen

    @property
    def depth(self):
        return 10 if self.output == 'hdr' or (self.purpose == 'archive' and self.codec != 'avc') else 8

    @property
    def pixel_format(self):
        if self.sampling == 'gray':
            return 'gray10le' if self.depth == 10 else 'gray'
        return f'yuv{self.sampling}p' + ('10le' if self.depth == 10 else '')


def resolve_format(options, media):
    if options.sampling not in {'auto', 'source', '420', '422', '444', 'gray'}:
        raise ValueError('请选择自动、保持原采样、4:2:0、4:2:2 或 4:4:4。')
    sampling = options.sampling
    if sampling == 'auto':
        sampling = 'source' if options.purpose == 'archive' else '420'
    if sampling == 'source':
        if media.pixel.get('flags', {}).get('alpha'):
            raise ValueError('素材含透明通道。请选择具体采样格式，输出为不带透明通道的视频。')
        sampling = source_sampling(media)
    if sampling not in {'420', '422', '444', 'gray'}:
        raise ValueError('当前编码器无法保持原采样，请手动选择 4:2:0、4:2:2 或 4:4:4。')
    if sampling != '420' and (options.purpose != 'archive' or options.codec == 'av1'):
        raise ValueError('保持此采样需要软件 HEVC 或 H.264；也可手动选择 4:2:0。')
    if options.color_range not in {'auto', 'source', 'limited', 'full'}:
        raise ValueError('请选择自动、保持原范围、Limited 或 Full。')
    data_range = options.color_range
    if data_range == 'auto':
        data_range = 'source' if options.purpose == 'archive' else 'limited'
    if data_range == 'source':
        fallback = PROFILES[options.profile][3] if options.transform.startswith('log-') and options.profile in PROFILES else None
        data_range = source_range(media, fallback)
    return replace(options, sampling=sampling, color_range=data_range)


def validate(options, media):
    options = resolve_format(options, media)
    if options.container not in {'auto', 'mp4', 'mkv'}:
        raise ValueError('请选择自动、MP4 或 MKV 封装。')
    if options.purpose not in {'share', 'archive'} or options.codec not in {'hevc', 'av1', 'avc'}:
        raise ValueError('请选择有效的用途和编码器。')
    if options.output not in {'sdr', 'hdr'}:
        raise ValueError('请选择 SDR 或 HDR 输出。')
    if options.output == 'hdr' and options.codec == 'avc':
        raise ValueError('HDR 输出请选择 HEVC 或 AV1。')
    if options.audio not in {'auto', 'copy', 'aac', 'opus', 'flac'}:
        raise ValueError('请选择有效的音轨格式。')
    if options.audio == 'flac':
        if options.container == 'mp4':
            raise ValueError('FLAC 音轨请选择 MKV 封装，以保留声道布局。')
        flac_tracks(media)
    if options.variant not in {'standard', 'fast'} or (options.variant == 'fast' and
            (options.purpose != 'archive' or options.codec != 'av1')):
        raise ValueError('快速备用质量档仅用于软件归档 AV1。')
    kind = media.kind if options.source_kind == 'auto' else options.source_kind
    if kind not in {'sdr', 'hlg', 'pq', 'log', 'unknown'}:
        raise ValueError('请选择有效的输入类型。')
    transform = options.transform
    if transform in {'log-hdr', 'log-sdr'}:
        if options.profile not in PROFILES:
            raise ValueError('请选择具体的 Log 曲线。')
        if transform != ('log-hdr' if options.output == 'hdr' else 'log-sdr'):
            raise ValueError('Log 转换目标需要与 SDR/HDR 输出一致。')
        if kind in HDR_KINDS:
            raise ValueError('输入已标为 HDR。请先核对输入类型，再选择 Log LUT。')
        if not re.search(r'(10|12|16)', media.video.get('pix_fmt', '')):
            raise ValueError('这些 Log 转换需要对应相机的高位深原片。')
        return 'log'
    if transform == 'hdr-sdr':
        if options.output != 'sdr' or kind not in HDR_KINDS:
            raise ValueError('HDR → SDR 需要 HLG/PQ 输入及 SDR 输出。')
    elif transform == 'none':
        if kind in {'log', 'unknown'}:
            raise ValueError('请确认输入类型。Log 原片需要选择曲线后转换。')
        if options.output == 'hdr' and kind == 'sdr':
            raise ValueError('SDR 输入请选择 SDR 输出。')
        if options.output == 'sdr' and kind in HDR_KINDS:
            raise ValueError('HDR 输入输出 SDR 时，请选择 HDR → SDR 转换。')
    else:
        raise ValueError('请选择有效的 LUT / 色调映射分组。')
    return kind


def frame_tags(colors):
    fields = {'color_primaries': 'color_primaries', 'color_transfer': 'color_trc',
              'color_space': 'colorspace', 'color_range': 'range'}
    values = []
    for key, target in fields.items():
        value = colors.get(key)
        if value not in UNSPECIFIED:
            if key == 'color_range':
                value = {'tv': 'limited', 'pc': 'full'}.get(value, value)
            values.append(f'{target}={value}')
    return 'setparams=' + ':'.join(values)


def tone_map_filter(data_range):
    return ','.join((
        'libplacebo=format=yuv444p16le:colorspace=bt709:color_primaries=bt709:'
        f'color_trc=bt709:range={data_range}:tonemapping=bt.2390:apply_dolbyvision=0',
        'sidedata=delete:type=MASTERING_DISPLAY_METADATA',
        'sidedata=delete:type=CONTENT_LIGHT_LEVEL',
        'sidedata=delete:type=DYNAMIC_HDR_PLUS',
    ))


def log_filter(video, profile):
    _, filename, default_matrix, default_range = PROFILES[profile]
    matrix = video.get('color_space')
    data_range = video.get('color_range')
    matrix = {'gbr': 'gbr', 'bt709': '709', 'bt2020nc': '2020_ncl', 'smpte170m': '170m', 'bt470bg': '470bg'}.get(matrix, default_matrix)
    data_range = {'pc': 'full', 'tv': 'limited'}.get(data_range, default_range)
    return ','.join((
        f'zscale=matrixin={matrix}:rangein={data_range}:range=full',
        'format=gbrpf32le',
        f'lut3d=file={filename}:interp=tetrahedral',
        'setparams=range=full:color_primaries=bt2020:color_trc=arib-std-b67:colorspace=gbr',
        'zscale=matrix=2020_ncl:rangein=full:range=limited:dither=error_diffusion',
        'format=yuv444p16le',
    ))


def build_command(tools, media, options, destination):
    kind = validate(options, media)
    options = resolve_format(options, media)
    video = dict(media.video)
    fallback = PROFILES[options.profile][3] if kind == 'log' else None
    video['color_range'] = 'pc' if source_range(media, fallback) == 'full' else 'tv'
    if kind in HDR_KINDS:
        video.update(color_transfer='arib-std-b67' if kind == 'hlg' else 'smpte2084')
        for name, value in HLG.items():
            if video.get(name) in UNSPECIFIED:
                video[name] = value
    elif kind == 'sdr':
        for name, value in REC709.items():
            if video.get(name) in UNSPECIFIED:
                video[name] = value
    if is_rgb(media):
        video['color_space'] = 'gbr'
    output_colors = dict(REC709) if options.output == 'sdr' else dict(HLG) if kind == 'log' else {
        key: video.get(key) for key in COLOR_FLAGS}
    if output_colors.get('color_space') == 'gbr':
        output_colors['color_space'] = 'bt2020nc' if options.output == 'hdr' else 'bt709'
    output_colors['color_range'] = 'pc' if options.color_range == 'full' else 'tv'
    side = side_data(media.video, media.frames)
    dynamic = [item.get('side_data_type', '') for item in side
               if any(term in item.get('side_data_type', '') for term in ('DOVI', 'Dolby Vision', 'HDR Dynamic'))]
    if dynamic and (options.transform != 'hdr-sdr' or any('HDR10+' not in item for item in dynamic)):
        raise ValueError('该素材带动态 HDR 元数据。HDR10+ 可选 HDR → SDR，Dolby Vision 需要专门流程。')

    config = CONFIG['archive_fast_av1'] if options.variant == 'fast' else CONFIG[options.purpose][options.codec]
    pixel_format = options.pixel_format
    filters = []
    color_conversion = any(video.get(key) != value for key, value in output_colors.items())
    software_frames = options.transform != 'none' or options.purpose == 'archive' or options.cpu_decode or color_conversion
    decoder = {'h264': 'h264_qsv', 'hevc': 'hevc_qsv', 'av1': 'av1_qsv'}.get(video.get('codec_name'))
    gpu_decode = not software_frames and decoder and video.get('pix_fmt') in {'yuv420p', 'yuv420p10le', 'p010le'}
    args = [tools.ffmpeg, '-hide_banner', '-nostdin', '-n', '-xerror', '-loglevel', 'warning',
            '-nostats', '-progress', 'pipe:1']
    if gpu_decode:
        args += ['-hwaccel', 'qsv', '-hwaccel_output_format', 'qsv', '-c:v', decoder]
    args += ['-i', str(media.path), '-map', f'0:{video["index"]}', '-map', '0:a?',
             '-map_metadata', '-1' if options.transform.startswith('log-') else '0',
             '-map_chapters', '0', '-sn', '-dn']
    if kind == 'log':
        if is_rgb(media):
            filters.append('format=gbrpf32le')
        filters.append(log_filter(video, options.profile))
    elif not gpu_decode:
        if is_rgb(media):
            filters.append('format=' + ('gbrpf32le' if media.pixel.get('flags', {}).get('float') else 'gbrp16le'))
        filters.append(frame_tags(video))
    if options.transform in {'hdr-sdr', 'log-sdr'}:
        filters.append(tone_map_filter('pc' if options.color_range == 'full' else 'tv'))

    if gpu_decode:
        qsv_format = 'p010le' if options.depth == 10 else 'nv12'
        color_options = ':out_range=' + output_colors['color_range']
        if options.output == 'sdr':
            color_options += ':out_color_matrix=bt709:out_color_primaries=bt709:out_color_transfer=bt709'
        filters.append(f'vpp_qsv=format={qsv_format}{color_options}')
    else:
        conversion = ['range=' + options.color_range, 'dither=error_diffusion']
        conversion += [target + '=' + output_colors[key] for key, target in
                       (('color_primaries', 'primaries'), ('color_transfer', 'transfer'), ('color_space', 'matrix'))
                       if output_colors.get(key) not in UNSPECIFIED]
        filters.append('zscale=' + ':'.join(conversion))
        filters.append(f'format={"p010le" if options.purpose == "share" and options.depth == 10 else pixel_format}')
        filters.append(frame_tags(output_colors))
    args += ['-vf', ','.join(filters), '-c:v', config['encoder'], '-preset', str(config['preset'])]
    if options.purpose == 'share':
        args += ['-global_quality:v', str(config['quality']), '-low_power', '1', '-async_depth', str(config.get('async_depth', 4)),
                 '-g', str(max(1, round(2 * nominal_fps(video))))]
        if options.codec == 'hevc':
            args += ['-profile:v', 'main10' if options.depth == 10 else 'main']
    else:
        args += ['-crf', str(config['quality']), '-pix_fmt', pixel_format, '-g', str(max(1, round(8 * nominal_fps(video))))]
        extra = dict(config.get('encoder_parameters', {}))
        if options.codec == 'hevc':
            extra = {'pools': 16, 'log-level': 'warning', **extra}
            metadata = hdr_metadata(side, 'hevc') if options.output == 'hdr' and kind == 'pq' else []
            if metadata:
                extra.update({'hdr10': 1, 'hdr10-opt': 1, 'repeat-headers': 1})
            args += ['-x265-params', ':'.join([f'{key}={value}' for key, value in extra.items()] + metadata)]
        elif options.codec == 'av1':
            extra = {'lp': config.get('lp', 0), 'tune': config.get('tune', 0), **extra}
            metadata = hdr_metadata(side, 'av1') if options.output == 'hdr' and kind == 'pq' else []
            args += ['-svtav1-params', ':'.join([f'{key}={value}' for key, value in extra.items()] + metadata)]
        elif extra:
            args += ['-x264-params', ':'.join(f'{key}={value}' for key, value in extra.items())]
    for name, flag in COLOR_FLAGS.items():
        if output_colors.get(name) not in UNSPECIFIED:
            args += [flag, output_colors[name]]
    args += ['-fps_mode:v', 'passthrough', '-enc_time_base:v', 'filter']
    audio = options.audio
    if audio == 'auto':
        audio = CONFIG['audio']['archive_default'] if options.purpose == 'archive' else CONFIG['audio']['share_default'][options.codec]
    if audio == 'copy':
        args += ['-c:a', 'copy']
    elif audio == 'flac':
        for index, (stream, depth) in enumerate(flac_tracks(media)):
            args += [f'-c:a:{index}', 'flac', f'-compression_level:a:{index}', '8',
                     f'-sample_fmt:a:{index}', 's16' if depth <= 16 else 's32',
                     f'-bits_per_raw_sample:a:{index}', str(depth)]
            if depth > 24:
                args += [f'-strict:a:{index}', 'experimental']
    else:
        policy = CONFIG['audio'][audio]
        args += ['-c:a', policy['encoder'], '-b:a', f'{policy["bitrate_kbps"]}k', '-ac', str(policy['channels'])]
        if audio == 'opus':
            args += ['-ar', str(policy['sample_rate']), '-vbr', policy['vbr'], '-application:a', policy['application'],
                     '-compression_level:a', str(policy['compression_level'])]
    if destination.suffix.lower() == '.mp4':
        if options.codec == 'hevc':
            args += ['-tag:v', 'hvc1']
        args += ['-movflags', '+faststart+write_colr']
    args += [str(destination)]
    return args, output_colors


def check_requirements(tools, options, command):
    encoder = CONFIG[options.purpose][options.codec]['encoder']
    result = subprocess.run([tools.ffmpeg, '-hide_banner', '-encoders'], capture_output=True,
                            encoding='utf-8', errors='replace', creationflags=NO_WINDOW, timeout=15)
    if result.returncode or not re.search(rf'\b{re.escape(encoder)}\b', result.stdout):
        raise ValueError(f'选中的 FFmpeg 缺少 {encoder} 编码器，请在工具路径里选择完整版本。')
    if options.audio == 'flac' and not re.search(r'\bflac\b', result.stdout):
        raise ValueError('选中的 FFmpeg 缺少 FLAC 编码器。')
    if options.purpose == 'archive':
        result = subprocess.run([tools.ffmpeg, '-hide_banner', '-h', 'encoder=' + encoder], capture_output=True,
                                encoding='utf-8', errors='replace', creationflags=NO_WINDOW, timeout=15)
        supported = re.search(r'Supported pixel formats:\s*([^\r\n]+)', result.stdout)
        if supported and options.pixel_format not in supported[1].split():
            raise ValueError(f'当前 FFmpeg 的 {encoder} 不支持 {options.pixel_format}，请更换编码器或采样格式。')
    required = {name for name in ('zscale', 'lut3d', 'libplacebo') if name + '=' in command[command.index('-vf') + 1]}
    if required:
        result = subprocess.run([tools.ffmpeg, '-hide_banner', '-filters'], capture_output=True,
                                encoding='utf-8', errors='replace', creationflags=NO_WINDOW, timeout=15)
        missing = [name for name in sorted(required) if not re.search(rf'\b{name}\b', result.stdout)]
        if result.returncode or missing:
            raise ValueError('选中的 FFmpeg 缺少所需滤镜 ' + ', '.join(missing) + '，请选择完整版本。')


def verify_result(tools, source, options, expected_colors, output):
    options = resolve_format(options, source)
    result = inspect_media(tools, output)
    if result.video.get('pix_fmt', '').replace('yuvj', 'yuv') != options.pixel_format and not (
            options.sampling == 'gray' and result.video.get('_monochrome')):
        raise ValueError(f'输出像素格式与选择不符，得到 {result.video.get("pix_fmt")}。')
    for key, value in expected_colors.items():
        if value not in UNSPECIFIED and result.video.get(key) != value:
            raise ValueError(f'输出色彩标签不符：{key}。')
    if options.audio == 'flac':
        before, after = flac_tracks(source), audio_streams(result)
        if len(before) != len(after) or any(target.get('codec_name') != 'flac' or
                target.get('sample_rate') != stream.get('sample_rate') or target.get('channels') != stream.get('channels') or
                stream.get('channel_layout') not in UNSPECIFIED and target.get('channel_layout') != stream.get('channel_layout') or
                int(target.get('bits_per_raw_sample') or 0) < depth
                for (stream, depth), target in zip(before, after)):
            raise ValueError('FLAC 输出的音轨、采样率、声道或位深与原片不符。')
    if options.output == 'sdr' and any(any(term in item.get('side_data_type', '') for term in
                                         ('HDR', 'Dolby', 'Mastering display metadata', 'Content light level metadata'))
                                       for item in side_data(result.video, result.frames)):
        raise ValueError('SDR 输出残留 HDR 元数据。')
    if options.output == 'hdr' and options.transform == 'none':
        before, after = side_data(source.video, source.frames), side_data(result.video, result.frames)
        static = {'Mastering display metadata', 'Content light level metadata'}
        wanted = {item.get('side_data_type') for item in before} & static
        if wanted - {item.get('side_data_type') for item in after}:
            raise ValueError('输出缺少源文件的 HDR 静态元数据。')
        if compare_static_metadata(before, after, result.video['codec_name']):
            raise ValueError('输出 HDR 元数据超出目标编码的精度容差。')
    return result


class Cancelled(Exception):
    pass


class Job:
    def __init__(self):
        self.lock = threading.Lock()
        self.cancelled = False
        self.process = None

    def cancel(self):
        with self.lock:
            self.cancelled = True
            if self.process is not None and self.process.poll() is None:
                self.process.terminate()

    def check_cancelled(self):
        if self.cancelled:
            raise Cancelled()

    def run(self, tools, media, options, output, emit=lambda event, value: None, seconds=None, overwrite=False):
        output = Path(output).resolve()
        if output == media.path.resolve() or (output.exists() and output.samefile(media.path)):
            raise ValueError('不能将源文件用作输出。')
        if output.exists() and (not overwrite or not output.is_file()):
            raise ValueError('输出文件已存在，请更换文件名。')
        if not output.parent.is_dir() or output.suffix.lower() != options.suffix:
            raise ValueError(f'请选择已有的输出目录，文件后缀应为 {options.suffix}。')
        validate(options, media)
        options = resolve_format(options, media)
        with tempfile.TemporaryDirectory(prefix='.video-converter-', dir=output.parent) as scratch:
            temporary = Path(scratch) / output.name
            command, expected = build_command(tools, media, options, temporary)
            check_requirements(tools, options, command)
            audio = options.audio
            if audio == 'auto':
                audio = CONFIG['audio']['archive_default'] if options.purpose == 'archive' else CONFIG['audio']['share_default'][options.codec]
            audio_streams = [stream for stream in media.streams if stream.get('codec_type') == 'audio']
            if options.suffix == '.mp4' and audio == 'copy' and audio_streams:
                self.check_cancelled()
                emit('status', '正在检查 MP4 音轨兼容性…')
                trial = Path(scratch) / 'audio-compatibility.mp4'
                probe_command = [tools.ffmpeg, '-hide_banner', '-nostdin', '-v', 'error', '-xerror', '-n',
                                 '-i', str(media.path), '-map', '0:a', '-vn', '-sn', '-dn',
                                 '-map_metadata', '-1', '-c:a', 'copy', '-t', '0.08', '-f', 'mp4', str(trial)]
                try:
                    compatibility = subprocess.run(probe_command, capture_output=True, encoding='utf-8',
                                                   errors='replace', creationflags=NO_WINDOW, timeout=20)
                except subprocess.TimeoutExpired:
                    self.check_cancelled()
                    raise ValueError('MP4 原音轨兼容性检测超时。请选择 AAC 音轨或 MKV 封装。') from None
                self.check_cancelled()
                if compatibility.returncode:
                    emit('log', compatibility.stderr.strip())
                    codecs = ', '.join(sorted({stream.get('codec_name', '未知') for stream in audio_streams}))
                    raise ValueError(f'当前 FFmpeg 无法将原音轨（{codecs}）直接封装为 MP4。请选择 AAC 音轨，或改用 MKV 封装。')
            if seconds:
                command[-1:-1] = ['-t', str(seconds)]
            emit('log', subprocess.list2cmdline(command))
            with self.lock:
                self.check_cancelled()
                process = subprocess.Popen(command, cwd=ASSETS, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                           encoding='utf-8', errors='replace', creationflags=NO_WINDOW)
                self.process = process
            progress = {}
            try:
                with process.stdout as stream:
                    for line in stream:
                        line = line.strip()
                        key, separator, value = line.partition('=')
                        if key in {'frame', 'out_time_us', 'speed', 'fps'}:
                            progress[key] = value.strip()
                        elif key == 'progress':
                            progress['duration'] = seconds or media.duration
                            emit('progress', dict(progress))
                        elif line and (not separator or key not in {'fps', 'stream_0_0_q', 'bitrate', 'total_size',
                                                                                'out_time_ms', 'out_time', 'dup_frames', 'drop_frames'}):
                            emit('log', line)
                code = process.wait()
            finally:
                with self.lock:
                    if process.poll() is None:
                        process.terminate()
                    process.wait()
                    self.process = None
            self.check_cancelled()
            if code:
                raise RuntimeError(f'FFmpeg 转码失败（{code}）。请查看日志，或改用软件归档 / 软件解码。')
            emit('status', '正在核对输出色彩和采样格式…')
            result = verify_result(tools, media, options, expected, temporary)
            with self.lock:
                self.check_cancelled()
                if output.exists() and not overwrite:
                    raise ValueError('输出文件已存在，请更换文件名。')
                if overwrite:
                    temporary.replace(output)
                else:
                    temporary.rename(output)
        return result
