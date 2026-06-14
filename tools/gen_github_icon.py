#!/usr/bin/env python3
"""Generates tools/icons/github.png - a dark GitHub-style badge for the gh.* nodes."""
from PIL import Image, ImageDraw
import os
S=256
im=Image.new('RGBA',(S,S),(0,0,0,0)); d=ImageDraw.Draw(im)
BG=(26,31,38,255); W=(255,255,255,255)
d.rounded_rectangle([4,4,S-4,S-4],radius=56,fill=BG)
d.polygon([(96,70),(78,34),(120,62)],fill=W)
d.polygon([(160,70),(178,34),(136,62)],fill=W)
d.ellipse([62,58,194,176],fill=W)
d.ellipse([74,150,182,214],fill=W)
d.ellipse([96,196,128,232],fill=BG)
d.ellipse([128,196,160,232],fill=BG)
d.line([(78,168),(54,196),(58,220),(80,224)],fill=W,width=11,joint='curve')
d.ellipse([104,98,122,118],fill=BG)
d.ellipse([134,98,152,118],fill=BG)
d.polygon([(122,124),(134,124),(128,134)],fill=BG)
out=os.path.join(os.path.dirname(__file__),"icons","github.png")
im.save(out); print("wrote",out)
