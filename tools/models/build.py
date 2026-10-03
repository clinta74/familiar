"""
Builds a character's 3D model: blender -b --factory-startup -P tools/models/build.py -- llama

Writes client/src/avatar/models/<name>.glb (committed: the web client and the phone apps load it)
and preview renders to tools/models/previews/ (not committed).
"""

import importlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import blendkit  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(HERE))


def main():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if not args:
        raise SystemExit("Which character? blender -b -P tools/models/build.py -- llama")
    for name in args:
        character = importlib.import_module(name)
        blendkit.reset()
        character.build()
        path = os.path.join(ROOT, "client", "src", "avatar", "models", f"{name}.glb")
        blendkit.export(path)
        print(f"wrote {path} ({os.path.getsize(path) // 1024} KB)")
        blendkit.previews(os.path.join(HERE, "previews"), name, character.FRAME)


main()
