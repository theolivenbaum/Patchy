#pragma once

namespace patchy::ui {

// Ends a CLI automation flow (--run-script, --export, --stress-test,
// --screenshot, each with or without --headless) with an exit code. Desktop
// platforms quit the event loop so app.exec() in main returns and the process
// exits normally. On wasm,
// QCoreApplication::exit cannot end the program: the build keeps Emscripten's
// default EXIT_RUNTIME=0, so when the Asyncify-resumed exec stack unwinds and
// main returns, the runtime silently stays alive with Qt already destroyed.
// No callback ever re-enters the wasm again and qtloader's onExit never
// fires, so the tab parks with a clean console and the page cannot tell the
// run finished (the --run-script soak wedge; see docs/wasm.md). On wasm this
// therefore calls emscripten_force_exit, which tears the runtime down for
// real: worker threads stop, Module.onExit delivers the exit code to the page
// (qtloader forwards it as qt.onExit), and no further wasm runs. Like a
// process exit, that skips destructors and any unflushed settings writes,
// which unattended CLI runs accept on every platform.
void exit_cli_application(int code);

// Ends the process right now with `code`: stdio flushed, no destructors, no
// atexit handlers (emscripten_force_exit on wasm). For the quit path only, when
// a tracked background worker is still blocked inside the OS after the bounded
// wait in main.cpp: destroying the QApplication under it would hand the worker
// dangling pointers, and waiting longer is the freeze this replaces. Everything
// the user could lose is already on disk by then (closeEvent flushed the
// settings; the caller drops the recovery folder first).
[[noreturn]] void end_process_without_destructors(int code);

}  // namespace patchy::ui
