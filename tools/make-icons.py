from pathlib import Path
from math import cos, sin, pi, hypot
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen

# Glint's 24px line icons. Keep the old codepoints so playback state changes use the same set.
icons = {}
pen = None


def polygon(points):
    pen.moveTo((points[0][0] * 40, (24 - points[0][1]) * 40))
    for x, y in points[1:]:
        pen.lineTo((x * 40, (24 - y) * 40))
    pen.closePath()


def circle(x, y, r):
    polygon([(x + cos(i * pi / 12) * r, y + sin(i * pi / 12) * r) for i in range(24)])


def line(*points):
    for (x, y), (a, b) in zip(points, points[1:]):
        length = hypot(a - x, b - y)
        dx, dy = (b - y) * .85 / length, (a - x) * .85 / length
        polygon([(x + dx, y - dy), (a + dx, b - dy), (a - dx, b + dy), (x - dx, y + dy)])
    for x, y in points:
        circle(x, y, .85)


def ring(x, y, r, start=0, end=2*pi):
    line(*[(x + cos(t) * r, y + sin(t) * r) for t in [start + (end-start)*i/40 for i in range(41)]])


def rect(x, y, w, h):
    line((x,y),(x+w,y),(x+w,y+h),(x,y+h),(x,y))


def icon(code, draw):
    global pen
    pen = TTGlyphPen(None)
    draw()
    icons[code] = pen.glyph()


icon(0xE700, lambda: [line((4,y),(20,y)) for y in (6,12,18)])
icon(0xE710, lambda: [line((5,12),(19,12)),line((12,5),(12,19))])
icon(0xE711, lambda: [line((6,6),(18,18)),line((18,6),(6,18))])
icon(0xE712, lambda: [circle(x,12,1.5) for x in (5,12,19)])
icon(0xE713, lambda: [ring(12,12,6),ring(12,12,2),*[line((12+cos(a)*7,12+sin(a)*7),(12+cos(a)*9,12+sin(a)*9)) for a in [i*pi/4 for i in range(8)]]])
icon(0xE70D, lambda: line((6,9),(12,15),(18,9)))
icon(0xE70E, lambda: line((6,15),(12,9),(18,15)))
icon(0xE72B, lambda: [line((20,12),(4,12)),line((10,6),(4,12),(10,18))])
icon(0xE72C, lambda: [ring(12,12,8,.2,5.5),line((20,4),(20,10),(14,10))])
icon(0xE73F, lambda: [line((3,8),(8,8),(8,3)),line((16,3),(16,8),(21,8)),line((21,16),(16,16),(16,21)),line((8,21),(8,16),(3,16))])
icon(0xE740, lambda: [line((4,9),(4,4),(9,4)),line((15,4),(20,4),(20,9)),line((20,15),(20,20),(15,20)),line((9,20),(4,20),(4,15))])
def speaker():
    line((10,5),(5,9),(2,9),(2,15),(5,15),(10,19),(10,5))
icon(0xE74F, lambda: [speaker(),line((15,9),(21,15)),line((21,9),(15,15))])
icon(0xE767, lambda: [speaker(),ring(11,12,6,-1,1),ring(11,12,10,-1,1)])
icon(0xE993, lambda: [speaker(),ring(11,12,6,-1,1)])
icon(0xE768, lambda: line((7,4),(20,12),(7,20),(7,4)))
icon(0xE769, lambda: [line((8,5),(8,19)),line((16,5),(16,19))])
icon(0xE76E, lambda: [ring(12,12,9),circle(9,9,1),circle(15,9,1),ring(12,12,5,.3,2.8)])
icon(0xE771, lambda: [line((4,7),(20,7)),line((4,17),(20,17)),circle(9,7,2),circle(15,17,2)])
icon(0xE774, lambda: [ring(12,12,9),ring(12,12,9),line((3,12),(21,12)),line((12,3),(8,8),(8,16),(12,21),(16,16),(16,8),(12,3))])
icon(0xE77A, lambda: [line((7,7),(7,20),(17,20),(17,7)),line((5,7),(19,7)),line((9,4),(15,4)),line((10,10),(10,17)),line((14,10),(14,17))])
icon(0xE77B, lambda: [ring(12,7,4),ring(12,21,8,pi,2*pi)])
icon(0xE718, lambda: line((9,3),(18,3),(16,9),(20,13),(13,15),(7,21),(9,14),(4,10),(9,8),(9,3)))
icon(0xE80F, lambda: [line((3,10),(12,3),(21,10)),line((5,9),(5,21),(10,21),(10,15),(14,15),(14,21),(19,21),(19,9))])
icon(0xE81C, lambda: [rect(3,4,18,16),line((3,9),(21,9)),line((8,4),(8,9)),line((16,4),(16,9)),line((10,13),(15,16),(10,19),(10,13))])
icon(0xE8A0, lambda: [rect(3,4,18,16),line((8,4),(8,20))])
icon(0xE714, lambda: [rect(3,5,18,14),line((10,9),(16,12),(10,15),(10,9))])
icon(0xE8A9, lambda: [rect(x,y,7,7) for x in (3,14) for y in (3,14)])
icon(0xE8A7, lambda: [line((10,4),(4,4),(4,20),(20,20),(20,14)),line((14,4),(20,4),(20,10)),line((20,4),(11,13))])
icon(0xE8A3, lambda: [rect(3,3,18,18),line((7,17),(17,7)),line((10,7),(17,7),(17,14))])
icon(0xE8BD, lambda: line((4,4),(20,4),(20,16),(11,16),(5,21),(5,16),(4,16),(4,4)))
icon(0xE71C, lambda: [ring(10,10,7),line((15,15),(21,21))])
icon(0xE8C6, lambda: [ring(6,6,3),ring(6,18,3),line((8,8),(21,20)),line((8,16),(21,4))])
icon(0xE8C8, lambda: [rect(8,8,13,13),line((4,16),(3,16),(3,3),(16,3),(16,4))])
icon(0xE8FD, lambda: [rect(3,4,18,16),line((8,4),(8,20)),line((12,9),(17,9)),line((12,14),(17,14))])
icon(0xE7F4, lambda: [rect(2,5,20,14),line((6,5),(6,19)),line((18,5),(18,19))])
icon(0xE7A7, lambda: [line((4,8),(20,8)),line((4,16),(20,16)),line((8,4),(8,20)),line((16,4),(16,20))])
icon(0xE895, lambda: [line((4,8),(20,8),(16,4)),line((20,16),(4,16),(8,20))])
icon(0xE896, lambda: [line((12,3),(12,16)),line((7,11),(12,16),(17,11)),line((4,17),(4,21),(20,21),(20,17))])
icon(0xEA8F, lambda: [line((5,17),(7,14),(7,8)),ring(12,8,5,pi,2*pi),line((17,8),(17,14),(19,17),(5,17)),ring(12,18,3,0,pi)])
icon(0xE945, lambda: [line((3,11),(16,5),(16,19),(3,13),(3,11)),line((7,15),(9,21)),line((20,8),(22,6)),line((20,16),(22,18))])
icon(0xE9F9, lambda: [line((3,19),(8,12),(13,15),(21,4)),line((15,4),(21,4),(21,10))])
icon(0xE9D9, lambda: [line((5,20),(5,12)),line((12,20),(12,4)),line((19,20),(19,8))])
icon(0xE910, lambda: [ring(12,12,9,.5,2*pi),ring(12,12,4),line((16,8),(16,15),(20,15))])

fb = FontBuilder(960, isTTF=True)
glyphs = {'.notdef': TTGlyphPen(None).glyph(), **{f'uni{c:04X}': g for c,g in icons.items()}}
fb.setupGlyphOrder(list(glyphs))
fb.setupCharacterMap({c:f'uni{c:04X}' for c in icons})
fb.setupGlyf(glyphs)
fb.setupHorizontalMetrics({name:(960,0) for name in glyphs})
fb.setupHorizontalHeader(ascent=960, descent=0)
fb.setupNameTable({'familyName':'Glint Icons','styleName':'Regular','uniqueFontIdentifier':'GlintIcons-Regular','fullName':'Glint Icons','psName':'GlintIcons-Regular'})
fb.setupOS2(sTypoAscender=960,sTypoDescender=0,usWinAscent=960,usWinDescent=0)
fb.setupPost()
fb.font['head'].created = fb.font['head'].modified = 3878064000
fb.save(Path(__file__).resolve().parents[1] / 'src/Glint/Assets/Fonts/GlintIcons.ttf')
