#pragma once

#include <QString>

#include <algorithm>
#include <functional>
#include <memory>
#include <string>
#include <vector>

class QWidget;

namespace patchy::ui {

// An edit owns its typed value, not a reference to a control or live layer.
// Only consecutive writes to the same property coalesce. Paint-kind changes
// and effect-stack operations must keep their ordering relative to parameters.
template <typename Settings>
struct AppearanceEdit {
  std::string key;
  std::function<void(Settings&)> apply;
};

template <typename Settings>
struct AppearanceEdits {
  std::vector<AppearanceEdit<Settings>> operations;

  void append(AppearanceEdit<Settings> edit) {
    if (!edit.key.empty() && !operations.empty() && operations.back().key == edit.key) {
      operations.back() = std::move(edit);
      return;
    }
    operations.push_back(std::move(edit));
  }

  [[nodiscard]] Settings applied(Settings value) const {
    for (const auto& operation : operations) operation.apply(value);
    return value;
  }
};

template <typename Settings>
struct AppearanceDialogContext {
  std::vector<Settings> originals;
  std::vector<QString> names;
  std::size_t selected_count{0};
  QString skipped_reason;
  QString notices;
};

// Named, typed accessors are shared by change capture, equality, mixed-state
// display and tests. Missing effect instances return null; ordinary parameter
// writes therefore never manufacture an effect.
template <typename Settings>
struct AppearanceProperty {
  std::string key;
  std::function<bool(const Settings&, const Settings&)> equal;
  std::function<AppearanceEdit<Settings>(const Settings&)> capture;
};

template <typename Settings, typename Value, typename Read, typename Write>
AppearanceProperty<Settings> appearance_property(std::string key, Read read, Write write) {
  return {key,
          [read](const Settings& a, const Settings& b) {
            const Value* left = read(a);
            const Value* right = read(b);
            return left && right ? *left == *right : left == right;
          },
          [key, read, write](const Settings& source) -> AppearanceEdit<Settings> {
            const Value* value = read(source);
            if (!value) return {key, [](Settings&) {}};
            return {key, [value = *value, write](Settings& target) { write(target, value); }};
          }};
}

template <typename Settings>
void capture_appearance_edits(AppearanceEdits<Settings>& edits,
                              const std::vector<AppearanceProperty<Settings>>& properties,
                              const Settings& before, const Settings& after,
                              const std::vector<std::string>& explicit_fields = {}) {
  for (const auto& property : properties) {
    if (!property.equal(before, after) ||
        std::find(explicit_fields.begin(), explicit_fields.end(), property.key) != explicit_fields.end()) {
      edits.append(property.capture(after));
    }
  }
}

// Tracks deliberate commits of unchanged values as well as valueChanged.
// Programmatic synchronization and a focus visit alone never count as edits.
void install_appearance_edit_intent(QWidget* widget, std::function<void()> committed);
void set_appearance_mixed(QWidget* widget, bool mixed);
QString appearance_selection_summary(std::size_t selected, std::size_t editable,
                                     const QString& reference, const QString& skipped);

}  // namespace patchy::ui
