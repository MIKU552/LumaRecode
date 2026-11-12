"""Source format inspection and lossless-audio eligibility."""
import re

from media import UNSPECIFIED


def source_sampling(media):
    if media.video.get('_monochrome'):
        return 'gray'
    pixel = media.pixel
    name = media.video.get('pix_fmt', '')
    flags = pixel.get('flags', {})
    if flags.get('bayer'):
        return 'unknown'
    if flags.get('rgb') or flags.get('palette') or name.startswith(('rgb', 'bgr', 'gbr', 'argb', 'abgr', 'rgba', 'bgra')) or name == 'pal8':
        return '444'
    if pixel.get('nb_components') in {1, 2} or name.startswith(('gray', 'ya')):
        return 'gray'
    if 'log2_chroma_w' in pixel and 'log2_chroma_h' in pixel:
        return {(1, 1): '420', (1, 0): '422', (0, 0): '444', (0, 1): '440', (2, 0): '411', (2, 2): '410'}.get(
            (pixel['log2_chroma_w'], pixel['log2_chroma_h']), 'unknown')
    match = re.search(r'(420|422|444|440|411|410)', name)
    return match[1] if match else {'nv12': '420', 'nv21': '420', 'p010le': '420', 'p012le': '420',
                                 'p016le': '420', 'yuyv422': '422', 'uyvy422': '422'}.get(name, 'unknown')


def is_rgb(media):
    return bool(media.pixel.get('flags', {}).get('rgb') or media.pixel.get('flags', {}).get('palette') or
                media.video.get('pix_fmt', '').startswith(('rgb', 'bgr', 'gbr', 'argb', 'abgr', 'rgba', 'bgra')) or
                media.video.get('pix_fmt') == 'pal8')


def source_depth(media):
    components = media.pixel.get('components', [])
    if media.pixel.get('flags', {}).get('alpha') and len(components) > 1:
        components = components[:-1]
    depths = [item['bit_depth'] for item in components if item.get('bit_depth', 0) > 0]
    raw_depth = 0
    for key in ('bits_per_raw_sample', 'bits_per_sample'):
        value = str(media.video.get(key, ''))
        if value.isdigit() and int(value) > 0:
            raw_depth = int(value)
            break
    if depths:
        return min(max(depths), raw_depth) if raw_depth else max(depths)
    if raw_depth: return raw_depth
    match = re.search(r'(?:p|gray)(10|12|14|16)', media.video.get('pix_fmt', ''))
    return int(match[1]) if match else 8


def sampling_choices(media):
    sampling = source_sampling(media)
    if sampling == 'gray': return ['gray', '420']
    shifts = {'444': (0, 0), '422': (1, 0), '420': (1, 1), '440': (0, 1), '411': (2, 0), '410': (2, 2)}
    original = shifts.get(sampling)
    return [name for name in ('420', '422', '444') if original is not None and
            all(target >= source for target, source in zip(shifts[name], original))]


def depth_choices(media, purpose, codec, output):
    supported = (8,) if purpose == 'share' and codec == 'avc' else (8, 10, 12) if purpose == 'archive' and codec == 'hevc' else (8, 10)
    ceiling = max(8, source_depth(media))
    return [depth for depth in supported if depth <= ceiling and (output != 'hdr' or depth >= 10)]


def default_depth(media, purpose, codec, output):
    choices = depth_choices(media, purpose, codec, output)
    if not choices: return None
    return max(choices) if purpose == 'archive' else 10 if output == 'hdr' else 8


def default_sampling(media, purpose, codec):
    if purpose == 'share' or codec == 'av1':
        return '420'
    sampling = source_sampling(media)
    if is_rgb(media):
        return '444'
    if sampling == '440': return '420'
    if media.pixel.get('flags', {}).get('alpha'):
        return sampling if sampling in {'420', '422', '444', 'gray'} else '420'
    return 'source'


def source_range(media, profile_range=None):
    value = media.video.get('color_range')
    if value not in UNSPECIFIED:
        if value in {'pc', 'full', 'jpeg'}: return 'full'
        if value in {'tv', 'limited', 'mpeg'}: return 'limited'
    if is_rgb(media) or media.video.get('pix_fmt', '').startswith('yuvj'):
        return 'full'
    return profile_range or ('full' if media.profile == 'milog' else 'limited')


def audio_streams(media):
    return [s for s in media.streams if s.get('codec_type') == 'audio']


def flac_tracks(media):
    streams = audio_streams(media)
    if not streams:
        raise ValueError('FLAC 需要素材包含音轨。')
    tracks = []
    lossless = {'flac', 'alac', 'ape', 'tta', 'tak', 'shorten', 'truehd', 'mlp', 'wmalossless'}
    for ordinal, stream in enumerate(streams, 1):
        codec = stream.get('codec_name', '')
        pcm = re.fullmatch(r'pcm_[su](\d+)(?:le|be)?(?:_planar)?', codec)
        confirmed = codec in lossless or pcm or (codec == 'dts' and stream.get('profile') == 'DTS-HD MA')
        if not confirmed or stream.get('sample_fmt', '').startswith(('flt', 'dbl')):
            raise ValueError(f'音轨 {ordinal}（{codec}）无法确认为 FLAC 支持的整数无损输入，请选择复制、AAC 或 Opus。')
        depth = next((int(stream[key]) for key in ('bits_per_raw_sample', 'bits_per_sample')
                      if str(stream.get(key, '')).isdigit() and int(stream[key]) > 0), int(pcm[1]) if pcm else 0)
        channels = int(stream.get('channels') or 0)
        if not 1 <= depth <= 32 or not 1 <= channels <= 8:
            raise ValueError(f'音轨 {ordinal} 需要明确的 1–32 位整数位深及 1–8 声道，才能转为 FLAC。')
        layout = stream.get('channel_layout')
        native = {1: 'mono', 2: 'stereo', 3: '3.0', 4: 'quad', 5: '5.0', 6: '5.1', 7: '6.1', 8: '7.1'}
        if (layout not in UNSPECIFIED and layout != native[channels]) or (channels > 2 and layout in UNSPECIFIED):
            raise ValueError(f'音轨 {ordinal} 的声道布局无法原样写入 FLAC，请选择复制原音轨。')
        tracks.append((stream, depth))
    return tracks


def flac_eligibility(media):
    try:
        flac_tracks(media)
        return True, ''
    except ValueError as error:
        return False, str(error)
