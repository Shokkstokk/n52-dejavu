"""Regenerates every icon the application ships, from icon.svg.

    python tools/icon-gen/makeicons.py

icon.svg beside this script is the source of truth. Everything under
src/DejaVu.App/Assets that shows the mark is generated from it and must not be hand-edited -
edit the SVG, run this, commit the result.

What it produces
----------------

    Assets/n52dejavu.ico        the application icon, eight sizes, on the darker plate
    Assets/tray-plate.png       the plate with every lamp dark
    Assets/tray-lamp-red.png    one lit lamp each, on a transparent canvas
    Assets/tray-lamp-green.png
    Assets/tray-lamp-blue.png

The tray is four files rather than eight pre-rendered keymaps. StatusIcon draws the plate and
then lays lit lamps over their own dark twins; every overlay is the same canvas and the same
path as the lamp beneath it, so a lit lamp covers exactly what it replaces. That means no
alignment arithmetic in the drawing code, and eight keymaps stay in step with the artwork
because there is only one plate and three lamps to keep in step.

Why generated art rather than drawing it in code
------------------------------------------------

StatusIcon used to draw the lamps with GDI+ - three rounded rectangles and a color each. That
was the right call for that shape. It stopped being the right call when the artwork grew a
four-stop gradient, a chamfered plate and chamfered lamps: reproducing those in code means a
second implementation of a drawing that already exists, and the two drift silently the moment
the SVG is touched. Compositing means what ships IS the drawing.

The cost is that the colors are baked, so the tray cannot follow a theme, and the assets are
fixed resolution. Both are accepted deliberately - the tray icon reports hardware state rather
than describing how the window looks, so it is not themeable by the same rule that keeps
Active, Inactive, EngineOn and EngineOff out of Palette.

Requires Inkscape. Nothing else - the ICO container is written here, and its DIB entries need
the pixels back out of the rendered PNGs, which is what the decoder below is for.
"""
import io
import os
import re
import struct
import subprocess
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..'))
SRC = os.path.join(HERE, 'icon.svg')
ASSETS = os.path.join(REPO, 'src', 'DejaVu.App', 'Assets')

INKSCAPE = next((p for p in (
    r'C:\Program Files\Inkscape\bin\inkscape.exe',
    r'C:\Program Files (x86)\Inkscape\bin\inkscape.exe',
) if os.path.exists(p)), None)

# The lit colors as they appear in icon.svg, and the dark values a lamp takes when it is off.
#
# The off values are not eyeballed. The eye weights the primaries very unequally - green
# carries about 71% of perceived luminance, red 21%, blue 7% - so three colors that look like
# equivalents in a picker are nowhere near equal in brightness, and an earlier set had the off
# blue reading as half lit next to a dead red. These sit within 1.12x of each other.
#
# Off blue is the exception to its own rule: matched on luminance it STILL read as lit,
# because saturated blues look brighter than their luminance says (Helmholtz-Kohlrausch). It
# is darker and slightly desaturated to compensate, which is why it carries a little red and
# green where the other two do not.
ON = {'red': 'ff1717', 'green': '00cc14', 'blue': '2e86ff'}
OFF = {'red': '560000', 'green': '002c0c', 'blue': '0d1442'}

# Rendered four times the 32-unit bitmap StatusIcon composes into, so the tray downsamples
# rather than resampling up from something smaller.
TRAY_SIZE = 128

# The plate is one gray in the tray and a darker one in the application icon, from one
# drawing. PLATE_TRAY is what icon.svg is authored in; PLATE_APP is substituted for the .ico.
#
# They are not the same job, and the difference is measurable rather than taste.
#
# The tray plate sits behind lamps that CHANGE, so it has to keep a lit lamp and an unlit lamp
# both readable - and those bracket any mid tone, so the plate cannot simply move toward
# either. Solved by sweeping plate luminance for the value that maximises the weakest of the
# six lamp states: 8.7%, which is about #535353. The first attempt was a light metal at 29%
# and it scored 1.14:1 on lit blue - the lamp visibly dissolved into the plate. This scores
# 1.96:1 at its worst.
#
# It is also NEUTRAL where the metal was cool - blue led red by 11 in every stop - because a
# blue lamp on a blue-tinted surface loses hue separation on top of luminance separation, and
# blue was already the weakest of the three.
#
# The application icon has no such constraint: it is static with every lamp lit, so the plate
# only has to serve lit lamps and can go darker. On the tray gray those same lamps score
# 1.97-3.52; on this one, 3.02-5.42.
#
# Substituted here rather than kept as a second SVG. Two files would be two things to edit and
# one of them would eventually be forgotten; this way the geometry has a single source and the
# only difference between the two treatments is four numbers, stated once.
#
# Both are built the same way: light at the top falling to dark at the bottom, with a step
# across the middle for a sheen, so each still reads as a surface rather than a flat tile.
PLATE_TRAY = ('#6e6e6e', '#575757', '#4e4e4e', '#3b3b3b')
PLATE_APP = ('#4a4f56', '#34383e', '#2b2f34', '#1c1f23')

# What Windows asks for. 256 goes in as PNG and the rest as DIBs: PNG entries are only
# reliably understood at the large sizes, and this is what the shell has always expected.
ICO_SIZES = [(256, True), (128, False), (64, False), (48, False),
             (32, False), (24, False), (20, False), (16, False)]


# ---------------------------------------------------------------------------------------
#  Rendering
# ---------------------------------------------------------------------------------------

def render(svg_text, out_path, size):
    """Runs one variant of the document through Inkscape at a given square size."""
    tmp = out_path + '.svg'
    io.open(tmp, 'w', encoding='utf-8').write(svg_text)
    try:
        subprocess.run([INKSCAPE, '--export-type=png', '--export-filename=' + out_path,
                        '--export-width=%d' % size, '--export-height=%d' % size,
                        '--export-background-opacity=0', tmp],
                       capture_output=True, check=False)
        if not os.path.exists(out_path):
            sys.exit('Inkscape produced nothing for %s' % os.path.basename(out_path))
    finally:
        os.remove(tmp)


def plate_for_app(svg):
    """Swaps the plate's gradient stops for the application icon's. Lamps are untouched."""
    for was, now in zip(PLATE_TRAY, PLATE_APP):
        stop = 'stop-color="%s"' % was
        if stop not in svg:
            sys.exit('plate stop %s not found - has icon.svg\'s gradient changed?' % was)
        svg = svg.replace(stop, 'stop-color="%s"' % now)
    return svg


def hide_fill(svg, value):
    """Hides one path by its fill, leaving the document otherwise intact."""
    return svg.replace('style="fill:%s' % value, 'style="display:none;fill:%s' % value, 1)


# ---------------------------------------------------------------------------------------
#  PNG -> pixels
# ---------------------------------------------------------------------------------------

def read_png(path):
    """Returns (width, height, rows) with rows as bytearrays of RGBA.

    Deliberately narrow: Inkscape writes 8-bit RGBA, non-interlaced, and anything else is a
    sign the pipeline changed rather than a case worth quietly handling.
    """
    data = io.open(path, 'rb').read()
    if data[:8] != b'\x89PNG\r\n\x1a\n':
        sys.exit('%s is not a PNG' % path)

    pos, idat, width, height = 8, [], None, None
    while pos < len(data):
        length, kind = struct.unpack('>I4s', data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        if kind == b'IHDR':
            width, height, depth, colour, _, _, interlace = struct.unpack('>IIBBBBB', body)
            if (depth, colour, interlace) != (8, 6, 0):
                sys.exit('%s: expected 8-bit RGBA non-interlaced, got depth=%d colour=%d '
                         'interlace=%d' % (path, depth, colour, interlace))
        elif kind == b'IDAT':
            idat.append(body)
        elif kind == b'IEND':
            break
        pos += 12 + length

    raw = zlib.decompress(b''.join(idat))

    # Un-filter. Each scanline is prefixed by its filter type; the four non-zero types are all
    # defined against the pixel to the left, the row above, or both.
    bpp, stride = 4, width * 4
    rows, prev, at = [], bytearray(stride), 0
    for _ in range(height):
        ftype = raw[at]
        line = bytearray(raw[at + 1:at + 1 + stride])
        at += 1 + stride
        if ftype == 1:
            for i in range(bpp, stride):
                line[i] = (line[i] + line[i - bpp]) & 0xFF
        elif ftype == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ftype == 3:
            for i in range(stride):
                left = line[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + ((left + prev[i]) >> 1)) & 0xFF
        elif ftype == 4:
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                b = prev[i]
                c = prev[i - bpp] if i >= bpp else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 0xFF
        elif ftype != 0:
            sys.exit('%s: unknown PNG filter %d' % (path, ftype))
        rows.append(line)
        prev = line
    return width, height, rows


# ---------------------------------------------------------------------------------------
#  The ICO container
# ---------------------------------------------------------------------------------------

def to_dib(path):
    """A BITMAPINFOHEADER, the colour rows bottom-up as BGRA, then a 1bpp AND mask.

    The mask is all zeros - nothing is transparent by the mask - because the alpha channel
    already carries transparency and every shell that reads 32-bit entries honours it.
    """
    width, height, rows = read_png(path)
    out = io.BytesIO()
    out.write(struct.pack('<IiiHHIIiiII',
                          40, width, height * 2, 1, 32, 0, 0, 0, 0, 0, 0))
    for row in reversed(rows):
        for x in range(width):
            r, g, b, a = row[x * 4:x * 4 + 4]
            out.write(bytes((b, g, r, a)))
    out.write(bytes(((width + 31) // 32) * 4 * height))
    return out.getvalue()


def write_ico(entries, out_path):
    """entries: list of (size, payload bytes)."""
    with io.open(out_path, 'wb') as f:
        f.write(struct.pack('<HHH', 0, 1, len(entries)))
        offset = 6 + 16 * len(entries)
        for size, payload in entries:
            d = 0 if size >= 256 else size
            f.write(struct.pack('<BBBBHHII', d, d, 0, 0, 1, 32, len(payload), offset))
            offset += len(payload)
        for _, payload in entries:
            f.write(payload)
    return offset


# ---------------------------------------------------------------------------------------

def main():
    if INKSCAPE is None:
        sys.exit('Inkscape not found. Install it, or edit INKSCAPE above.')

    base = io.open(SRC, encoding='utf-8').read()
    work = os.path.join(HERE, '_build')
    os.makedirs(work, exist_ok=True)

    print('tray:')

    # The plate, every lamp dark.
    plate = base
    for name in ON:
        plate = plate.replace('fill:#' + ON[name], 'fill:#' + OFF[name], 1)
    dest = os.path.join(ASSETS, 'tray-plate.png')
    render(plate, dest, TRAY_SIZE)
    print('  %-22s %d bytes' % ('tray-plate.png', os.path.getsize(dest)))

    # One lit lamp each, plate and the other lamps hidden.
    for name in ('red', 'green', 'blue'):
        s = hide_fill(base, 'url(#metal)')
        for other in ON:
            if other != name:
                s = hide_fill(s, '#' + ON[other])
        dest = os.path.join(ASSETS, 'tray-lamp-%s.png' % name)
        render(s, dest, TRAY_SIZE)
        print('  %-22s %d bytes' % ('tray-lamp-%s.png' % name, os.path.getsize(dest)))

    print('icon:')
    app = plate_for_app(base)
    entries = []
    for size, as_png in ICO_SIZES:
        png = os.path.join(work, 'icon-%d.png' % size)
        render(app, png, size)
        payload = io.open(png, 'rb').read() if as_png else to_dib(png)
        entries.append((size, payload))
        print('  %3d x %-3d %10d bytes  %s' % (size, size, len(payload),
                                               'png' if as_png else 'dib'))

    out = os.path.join(ASSETS, 'n52dejavu.ico')
    total = write_ico(entries, out)
    actual = os.path.getsize(out)
    if actual != total:
        sys.exit('wrote %d bytes, expected %d - an entry was truncated' % (actual, total))
    print('  %-22s %d bytes, %d sizes' % ('n52dejavu.ico', actual, len(entries)))

    for f in os.listdir(work):
        os.remove(os.path.join(work, f))
    os.rmdir(work)


if __name__ == '__main__':
    main()
