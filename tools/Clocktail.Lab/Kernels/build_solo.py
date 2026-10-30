"""Build the lab's soloist kernel once: solo.cu compiled by NVRTC to PTX (compute_80) and written to solo.ptx beside it,
which the lab embeds; the driver compiles the PTX for the card at run time, as it does the lab's other kernels.

    pip install nvidia-cuda-nvrtc        (or set CLOCKTAIL_NVRTC to an nvrtc64_*.dll)
    python tools\\Clocktail.Lab\\Kernels\\build_solo.py

The PTX's .version is lowered to the oldest the instructions need (8.0) so an older driver's JIT still takes it.
"""
import ctypes
import glob
import hashlib
import os
import re
import site
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent


def find_nvrtc() -> str:
    env = os.environ.get("CLOCKTAIL_NVRTC")
    if env and os.path.exists(env):
        return env
    roots = []
    for d in site.getsitepackages() + [site.getusersitepackages()]:
        roots += [os.path.join(d, "nvidia", "cu13", "bin", "x86_64"), os.path.join(d, "nvidia", "cu13", "bin"),
                  os.path.join(d, "nvidia", "cuda_nvrtc", "bin")]
    roots += os.environ.get("PATH", "").split(os.pathsep)
    for r in roots:
        hits = sorted(h for h in glob.glob(os.path.join(r, "nvrtc64_1*.dll")) if "builtins" not in h and ".alt." not in h)
        if hits:
            return hits[-1]
    raise FileNotFoundError("NVRTC not found: pip install nvidia-cuda-nvrtc, or set CLOCKTAIL_NVRTC")


def main() -> int:
    path = find_nvrtc()
    os.add_dll_directory(os.path.dirname(path))
    keep = [ctypes.CDLL(p) for p in glob.glob(os.path.join(os.path.dirname(path), "nvrtc-builtins64_*.dll"))]
    rt = ctypes.CDLL(path)
    src = (HERE / "solo.cu").read_text(encoding="utf-8")
    prog = ctypes.c_void_p()
    assert rt.nvrtcCreateProgram(ctypes.byref(prog), src.encode(), b"solo.cu", 0, None, None) == 0
    opts = [b"-arch=compute_80", b"-std=c++17", b"-default-device"]
    argv = (ctypes.c_char_p * len(opts))(*opts)
    rc = rt.nvrtcCompileProgram(prog, len(opts), argv)
    n = ctypes.c_size_t()
    rt.nvrtcGetProgramLogSize(prog, ctypes.byref(n))
    log = ctypes.create_string_buffer(n.value)
    rt.nvrtcGetProgramLog(prog, log)
    if rc != 0:
        print(log.value.decode(errors="replace"), file=sys.stderr)
        return 1
    rt.nvrtcGetPTXSize(prog, ctypes.byref(n))
    ptx = ctypes.create_string_buffer(n.value)
    rt.nvrtcGetPTX(prog, ptx)
    text = ptx.value.decode("ascii")
    text = re.sub(r"^\.version \d+\.\d+$", ".version 8.0", text, count=1, flags=re.M)
    header = (f"// solo.ptx: built by build_solo.py from solo.cu (sha256 {hashlib.sha256(src.encode()).hexdigest()[:16]}),"
              f" NVRTC {os.path.basename(path)}, -arch=compute_80. Do not edit; rebuild.\n")
    (HERE / "solo.ptx").write_text(header + text, encoding="ascii", newline="\n")
    print(f"solo.ptx: {len(text)} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
