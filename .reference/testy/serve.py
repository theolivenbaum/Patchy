"""Serve the Testy dashboard (run index + all past reports) without running a benchmark.

  python testy\\serve.py [port]

testy.py starts the same server itself during runs; this exists so the run index
stays browsable between runs (the port comes from config.local.json, default 8901).
Binding is exclusive, so if a benchmark's own server already holds the port this
simply picks the next one.
"""

from __future__ import annotations

import sys
import signal
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import config
from testy import start_server


def main() -> int:
    port = int(sys.argv[1]) if len(sys.argv) > 1 else config.PORT
    server, bound = start_server(port)
    print(f"[testy] dashboard: http://127.0.0.1:{bound}/  (Ctrl+C to stop)", flush=True)
    previous_sigint = signal.signal(signal.SIGINT, lambda _signum, _frame: server.request_stop())
    try:
        server.wait_until_stopped()
    finally:
        server.request_stop()
        server.wait_until_stopped()
        signal.signal(signal.SIGINT, previous_sigint)
    return 0


if __name__ == "__main__":
    sys.exit(main())
