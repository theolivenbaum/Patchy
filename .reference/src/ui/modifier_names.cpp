#include "ui/modifier_names.hpp"

#include <QCoreApplication>
#include <QLatin1StringView>

namespace patchy::ui {

// Translatable so a locale can spell the macOS keys its own way; Apple Japan writes
// "command" and "option" in Latin script beside the katakana, so the default stands.
// The context is spelled out at each call because lupdate cannot read it from a
// constant.

QString ctrl_key_name() {
#ifdef Q_OS_MACOS
  return QCoreApplication::translate("patchy::ui::ModifierNames", "Command");
#else
  return QCoreApplication::translate("patchy::ui::ModifierNames", "Ctrl");
#endif
}

QString alt_key_name() {
#ifdef Q_OS_MACOS
  return QCoreApplication::translate("patchy::ui::ModifierNames", "Option");
#else
  return QCoreApplication::translate("patchy::ui::ModifierNames", "Alt");
#endif
}

QString resolve_modifier_names(QString text) {
  // replace() is a no-op when the token is absent, so both passes always run rather than
  // paying for a contains() probe first.
  text.replace(QLatin1StringView(kCtrlModifierToken), ctrl_key_name());
  text.replace(QLatin1StringView(kAltModifierToken), alt_key_name());
  return text;
}

}  // namespace patchy::ui
