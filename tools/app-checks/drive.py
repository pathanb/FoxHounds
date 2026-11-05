#!/usr/bin/env python3
# Drives the app on a virtual display with real X input (XTEST): keys, clicks and screenshots.
# Usage: drive.py DISPLAY STEP...   with steps: key:NAME[:COUNT]  click:X:Y  move:X:Y  wait:SECONDS  shot:PATH
import subprocess, sys, time
from Xlib import X, XK, display
from Xlib.ext import xtest

d = display.Display(sys.argv[1])

def key(name, count=1):
    code = d.keysym_to_keycode(XK.string_to_keysym(name))
    for _ in range(count):
        xtest.fake_input(d, X.KeyPress, code); d.sync(); time.sleep(0.04)   # held over two frames at 60 fps
        xtest.fake_input(d, X.KeyRelease, code); d.sync(); time.sleep(0.04)

def move(x, y):
    xtest.fake_input(d, X.MotionNotify, x=x, y=y); d.sync(); time.sleep(0.05)

def click(x, y):
    move(x, y)
    xtest.fake_input(d, X.ButtonPress, 1); d.sync(); time.sleep(0.05)
    xtest.fake_input(d, X.ButtonRelease, 1); d.sync(); time.sleep(0.1)

for step in sys.argv[2:]:
    kind, *args = step.split(":")
    if kind == "key":
        key(args[0], int(args[1]) if len(args) > 1 else 1)
    elif kind == "click":
        click(int(args[0]), int(args[1]))
    elif kind == "move":
        move(int(args[0]), int(args[1]))
    elif kind == "wait":
        time.sleep(float(args[0]))
    elif kind == "shot":
        time.sleep(0.15)
        subprocess.run(["import", "-display", sys.argv[1], "-window", "root", "-crop", "1100x760+0+0", args[0]], check=True)
    else:
        raise SystemExit(f"unknown step {step}")
