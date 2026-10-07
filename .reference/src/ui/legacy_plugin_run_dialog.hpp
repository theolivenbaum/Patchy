#pragma once

// The companion box shown while a legacy Photoshop plug-in runs in its helper
// process (docs/plugins.md). It tells the user what is going on in words that
// match the plug-in's phase: "adjust its settings and click OK" while the
// plug-in's own window is up (no busy animation, since Patchy is waiting for
// the user, not working), a busy bar or percentage while the filter applies.
// The stop button is "Force Stop Plug-in" while the plug-in waits for the user
// (the run can only be ended by stopping the helper then) and "Cancel" while it
// works. The box never takes keyboard focus, so it can never push the plug-in's
// window, which lives in another process, behind Patchy. Pure Qt: the state
// logic is exercised offscreen by ui_legacy_plugin_run_dialog_states.

#include "ui/legacy_plugin_runner.hpp"

#include <QDialog>
#include <QRect>
#include <QString>

class QLabel;
class QProgressBar;
class QPushButton;

namespace patchy::ui {

class LegacyPluginRunDialog : public QDialog {
  Q_OBJECT

public:
  enum class State {
    Starting,        // helper launching, plug-in loading, no window yet
    WaitingForUser,  // the plug-in's window is up and waits for the user
    Applying,        // the filtering pass
  };

  explicit LegacyPluginRunDialog(const QString& plugin_name, QWidget* parent = nullptr);

  // Inputs from the runner callbacks.
  void set_phase(LegacyPluginPhase phase);
  // The plug-in's largest visible window in physical screen pixels; an invalid
  // rectangle means none is up. The first valid one reveals the box and places
  // it just below the window.
  void set_plugin_window(const QRect& screen_rect);
  void set_progress(int done, int total);
  // Shows the box unless the user already asked to stop (the caller calls it
  // once the run outlasts a quick filter; a plug-in window reveals it at once).
  void reveal();

  // Outputs polled by the runner.
  [[nodiscard]] bool stop_requested() const noexcept { return stop_requested_; }
  // True once per click of "Show Plug-in Window".
  [[nodiscard]] bool take_show_window_request() noexcept;

  [[nodiscard]] State state() const noexcept { return state_; }

protected:
  // The window's close button and the stop button both end here.
  void done(int result) override;

private:
  void update_state();
  void place_below_plugin_window();

  QString plugin_name_;
  QLabel* message_{nullptr};
  QLabel* hint_{nullptr};
  QProgressBar* bar_{nullptr};
  QPushButton* show_window_button_{nullptr};
  QPushButton* stop_button_{nullptr};
  State state_{State::Starting};
  LegacyPluginPhase phase_{LegacyPluginPhase::Unknown};
  QRect plugin_window_rect_;
  bool progress_seen_{false};
  bool stop_requested_{false};
  bool show_window_requested_{false};
  bool placed_below_plugin_{false};
};

}  // namespace patchy::ui
