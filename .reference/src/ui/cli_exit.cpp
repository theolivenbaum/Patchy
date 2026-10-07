#include "ui/cli_exit.hpp"

#include <QCoreApplication>

#include <cstdio>
#include <cstdlib>

#ifdef Q_OS_WASM
#include <emscripten.h>
#endif

namespace patchy::ui {

void end_process_without_destructors(int code) {
  std::fflush(nullptr);
#ifdef Q_OS_WASM
  emscripten_force_exit(code);
#else
  std::_Exit(code);
#endif
}

void exit_cli_application(int code) {
#ifdef Q_OS_WASM
  // QCoreApplication::exit leaves a parked tab here (see the header); shut the
  // Emscripten runtime down instead so the page learns the exit code.
  emscripten_force_exit(code);
#else
  QCoreApplication::exit(code);
#endif
}

}  // namespace patchy::ui
