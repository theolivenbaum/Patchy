#pragma once

// PATCHY_UI_PROFILE stderr timing lines, in a header light enough for the import
// and export code that must not pull in main_window_shared.hpp (CanvasWidget and
// friends). The definition of log_ui_profile lives in main_window_shared.cpp.

#include <chrono>
#include <string_view>

namespace patchy::ui {

// PATCHY_UI_PROFILE stderr timing lines (no-op unless the env var is set).
void log_ui_profile(std::string_view stage, double elapsed_ms, std::string_view detail = {});

// Scoped variant for functions with several returns: logs on destruction. The stage
// is held as a view, so pass a literal (or anything that outlives the scope).
class UiProfileScope {
 public:
  explicit UiProfileScope(std::string_view stage, std::string_view detail = {})
      : stage_(stage), detail_(detail), started_(std::chrono::steady_clock::now()) {}
  UiProfileScope(const UiProfileScope&) = delete;
  UiProfileScope& operator=(const UiProfileScope&) = delete;
  ~UiProfileScope() {
    log_ui_profile(stage_,
                   std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - started_).count(),
                   detail_);
  }

 private:
  std::string_view stage_;
  std::string_view detail_;
  std::chrono::steady_clock::time_point started_;
};

}  // namespace patchy::ui
