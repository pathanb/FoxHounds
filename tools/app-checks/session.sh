#!/bin/bash
# Runs the desktop app on a virtual display and drives it with real X input (see drive.py), for the manual checks of
# spec 16.1 without a screen. Needs Xvfb, ImageMagick's import and python-xlib (pip install python-xlib).
#
#   tools/app-checks/session.sh NAME APP-ARGS... -- STEPS...
#   tools/app-checks/session.sh keys --data /tmp/fh --replay game.fhg -- key:Home shot:/tmp/home.png key:Right:57 shot:/tmp/end.png
#
# The app is the Release build; the window is 1100 x 760 at the top left of a display of the same size.
set -u
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
name=$1; shift
app=(); while [ $# -gt 0 ] && [ "$1" != "--" ]; do app+=("$1"); shift; done; shift
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" PATH="$HOME/.dotnet:$PATH"
display=:${FH_DISPLAY:-77} # not 99, which xvfb-run -a takes first
Xvfb "$display" -screen 0 1100x760x24 >"/tmp/fh-xvfb-$name.log" 2>&1 &
xvfb=$!
sleep 1
DISPLAY=$display "$repo/src/FoxHounds.App/bin/Release/net10.0/FoxHounds" "${app[@]}" >"/tmp/fh-app-$name.log" 2>&1 &
appPid=$!
sleep 4
python3 "$here/drive.py" "$display" move:500:380 "$@"
status=$?
kill $appPid 2>/dev/null; wait $appPid 2>/dev/null
kill $xvfb 2>/dev/null; wait $xvfb 2>/dev/null
exit $status
