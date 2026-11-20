#!/usr/bin/env python3
import json
import os
import sys

if "--version" in sys.argv:
    sys.stdout.write("bubblewrap 0.8.0\n")
    sys.stdout.flush()
    sys.exit(0)

capture_path = os.environ.get("PERGA_TEST_BWRAP_CAPTURE")
if capture_path:
    with open(capture_path, "w", encoding="utf-8") as f:
        json.dump(sys.argv[1:], f)

sep_idx = sys.argv.index("--")
chdir_val = None
for i in range(1, sep_idx - 1):
    if sys.argv[i] == "--chdir":
        chdir_val = sys.argv[i + 1]

if chdir_val:
    os.chdir(chdir_val)

inner_cmd = sys.argv[sep_idx + 1:]
os.execvp(inner_cmd[0], inner_cmd)
