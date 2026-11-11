import fractions

def side_data(video, frames):
    return video.get('side_data_list', []) + [entry for frame in frames for entry in frame.get('side_data_list', [])]


def nominal_fps(video):
    for key in ('avg_frame_rate', 'r_frame_rate'):
        try:
            rate = fractions.Fraction(video.get(key, '0'))
        except (ValueError, ZeroDivisionError):
            continue
        if rate > 0:
            return float(rate)
    return 30

def hdr_metadata(side,codec):
    md=next((s for s in side if s.get('side_data_type')=='Mastering display metadata'),None)
    cl=next((s for s in side if s.get('side_data_type')=='Content light level metadata'),None)
    out=[]
    value=lambda name:float(fractions.Fraction(md[name]))
    if md and all(k in md for k in ['green_x','green_y','blue_x','blue_y','red_x','red_y','white_point_x','white_point_y','max_luminance','min_luminance']):
        values=[value(k) for k in ['green_x','green_y','blue_x','blue_y','red_x','red_y','white_point_x','white_point_y','max_luminance','min_luminance']]
        if codec=='hevc':
            values=[str(round(v*(50000 if i<8 else 10000))) for i,v in enumerate(values)];key='master-display'
        else:values=[f'{v:.8f}' for v in values];key='mastering-display'
        out.append(key+'=G(%s,%s)B(%s,%s)R(%s,%s)WP(%s,%s)L(%s,%s)'%tuple(values))
    if cl and 'max_content' in cl and 'max_average' in cl:
        out.append(('max-cll' if codec=='hevc' else 'content-light')+f"={cl['max_content']},{cl['max_average']}")
    return out


def compare_static_metadata(before_side,after_side,output_codec):
    """Allow only the target bitstream format's metadata quantization error."""
    errors=[]
    for kind in ['Mastering display metadata','Content light level metadata']:
        before=next((s for s in before_side if s.get('side_data_type')==kind),None)
        after=next((s for s in after_side if s.get('side_data_type')==kind),None)
        if not before or not after:continue  # Missing types are checked separately.
        for key,value in before.items():
            if key=='side_data_type':continue
            if key not in after:
                errors.append({'type':kind,'field':key,'reason':'missing field'});continue
            a=float(fractions.Fraction(str(value)));b=float(fractions.Fraction(str(after[key])))
            if kind=='Content light level metadata':tolerance=0
            elif key=='max_luminance':tolerance=1/256 if output_codec=='av1' else 1/10000
            elif key=='min_luminance':tolerance=1/16384 if output_codec=='av1' else 1/10000
            else:tolerance=1/65536 if output_codec=='av1' else 1/50000
            if abs(a-b)>tolerance+1e-12:
                errors.append({'type':kind,'field':key,'before':value,'after':after[key],'tolerance':tolerance})
    return errors
