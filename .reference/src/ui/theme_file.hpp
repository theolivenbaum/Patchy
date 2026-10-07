#pragma once

// User-importable theme files: a JSON document declaring a base built-in
// color scheme plus a full set of @role_name color overrides (see
// theme_palette.hpp and the "Color scheme" section of docs/ui-conventions.md).
//
// A role the file omits keeps the base scheme's value; an unknown role name
// is tolerated (skipped, reported as a warning) so a file written by a newer
// build still loads in an older one and the reverse. Colors are strict
// #RRGGBB or #RRGGBBAA; anything else is a hard load error naming the role.

#include "ui/theme_palette.hpp"

#include <QByteArray>
#include <QColor>
#include <QString>
#include <QStringList>

#include <optional>

namespace patchy::ui {

// The "format" key a theme file carries. A file without one is format 1. Bump
// only for a change an older build could not read correctly; adding roles never
// needs it (unknown roles are ignored, missing ones fall back to the base).
inline constexpr int kThemeFileFormat = 1;

struct CustomTheme {
  QString name;
  ColorScheme base = ColorScheme::Dark;
  ThemePalette palette;
};

struct ThemeLoadResult {
  std::optional<CustomTheme> theme;
  QString error;         // non-empty means `theme` is unset; names the failure.
  QStringList warnings;  // non-fatal: unknown role names, etc.
};

// Strict "#RRGGBB" / "#RRGGBBAA" parse. Deliberately not QColor::fromString:
// that also accepts SVG color names and "#RGB", which the file format does not.
[[nodiscard]] std::optional<QColor> parse_theme_color(const QString& text);

// Parses a theme file. `theme.palette` starts as a copy of theme(base), so
// every role the file omits keeps that scheme's value.
[[nodiscard]] ThemeLoadResult load_theme_from_json(const QByteArray& json);

// Serializes every role in `palette` (Export writes the full, current set of
// values, never a diff against `base`).
[[nodiscard]] QByteArray serialize_theme_to_json(const ThemePalette& palette, ColorScheme base,
                                                  const QString& name);

// Desktop persistence directory for imported theme files; empty on wasm (no
// AppData store there -- the theme buttons are desktop-only, see
// docs/ui-conventions.md). Honors the PATCHY_THEMES_DIR environment override
// for test isolation, like PATCHY_RECOVERY_DIR (document_recovery.hpp).
[[nodiscard]] QString user_themes_directory();

// A theme id, the value ThemeManager persists, is either a file name within
// user_themes_directory() or kBundledThemeIdPrefix followed by the name of a
// theme compiled into the binary (themes.qrc, the files in themes/bundled/).
// Bundled themes exist on every platform, wasm included.
inline constexpr QLatin1StringView kBundledThemeIdPrefix{"bundled:"};
[[nodiscard]] bool is_bundled_theme_id(const QString& id);
// The bundled theme file names, in the order the Preferences combo lists them.
[[nodiscard]] QStringList bundled_theme_file_names();
// The path (a resource path for a bundled id) the id reads from; empty when a
// user id has no directory (wasm).
[[nodiscard]] QString theme_file_path_for_id(const QString& id);
// Reads and parses the file behind an id. A missing or unreadable file is an
// error like any other, so callers have one branch.
[[nodiscard]] ThemeLoadResult load_theme_by_id(const QString& id);

}  // namespace patchy::ui
