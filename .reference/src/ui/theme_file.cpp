#include "ui/theme_file.hpp"

#include <QCoreApplication>
#include <QDir>
#include <QFile>
#include <QHash>
#include <QIODevice>
#include <QJsonDocument>
#include <QJsonObject>
#include <QJsonParseError>
#include <QJsonValue>
#include <QStandardPaths>

// The bundled theme resources (themes.qrc) live in the static patchy_ui
// library; force registration before first use, as icon_theme.cpp does for
// icons.qrc.
int qInitResources_themes();

namespace patchy::ui {

namespace {

void ensure_theme_resources() {
  static const int registered = ::qInitResources_themes();
  (void)registered;
}

[[nodiscard]] std::optional<ColorScheme> parse_base_token(const QString& token) {
  if (token == QStringLiteral("dark")) {
    return ColorScheme::Dark;
  }
  if (token == QStringLiteral("light")) {
    return ColorScheme::Light;
  }
  return std::nullopt;
}

[[nodiscard]] QString base_token(ColorScheme scheme) {
  return scheme == ColorScheme::Light ? QStringLiteral("light") : QStringLiteral("dark");
}

[[nodiscard]] bool is_hex_digit(QChar c) {
  return (c >= QLatin1Char('0') && c <= QLatin1Char('9')) || (c >= QLatin1Char('a') && c <= QLatin1Char('f')) ||
         (c >= QLatin1Char('A') && c <= QLatin1Char('F'));
}

[[nodiscard]] QString color_to_hex(QColor color) {
  const auto byte = [](int value) { return QStringLiteral("%1").arg(value, 2, 16, QLatin1Char('0')); };
  auto text = QLatin1Char('#') + byte(color.red()) + byte(color.green()) + byte(color.blue());
  if (color.alpha() != 255) {
    text += byte(color.alpha());
  }
  return text;
}

// Mirrors role_members() in theme_qss.cpp: a name-to-member lookup built once
// from the same authoritative table.
const QHash<QString, QColor ThemePalette::*>& role_members() {
  static const auto members = [] {
    QHash<QString, QColor ThemePalette::*> table;
    for (const auto& [name, member] : theme_palette_roles()) {
      table.insert(QString(name), member);
    }
    return table;
  }();
  return members;
}

}  // namespace

std::optional<QColor> parse_theme_color(const QString& text) {
  if ((text.size() != 7 && text.size() != 9) || text.isEmpty() || text[0] != QLatin1Char('#')) {
    return std::nullopt;
  }
  for (int i = 1; i < text.size(); ++i) {
    if (!is_hex_digit(text[i])) {
      return std::nullopt;
    }
  }
  bool ok = false;
  const auto rgb = text.mid(1, 6).toUInt(&ok, 16);
  if (!ok) {
    return std::nullopt;
  }
  auto color = QColor::fromRgb(rgb);
  if (text.size() == 9) {
    const auto alpha = text.mid(7, 2).toUInt(&ok, 16);
    if (!ok) {
      return std::nullopt;
    }
    color.setAlpha(static_cast<int>(alpha));
  }
  return color;
}

ThemeLoadResult load_theme_from_json(const QByteArray& json) {
  ThemeLoadResult result;

  QJsonParseError parse_error;
  const auto document = QJsonDocument::fromJson(json, &parse_error);
  if (parse_error.error != QJsonParseError::NoError || !document.isObject()) {
    result.error = QCoreApplication::translate("ThemeFile", "Not a valid theme file: %1")
                       .arg(parse_error.error != QJsonParseError::NoError
                                ? parse_error.errorString()
                                : QCoreApplication::translate("ThemeFile", "the top level is not an object"));
    return result;
  }

  const auto object = document.object();
  // "format" is optional (an absent one is format 1, the shape this build
  // writes); any other value means a newer build wrote keys this one cannot
  // interpret, which is a hard error rather than a silent partial load.
  const auto format_value = object.value(QStringLiteral("format"));
  if (!format_value.isUndefined() && (!format_value.isDouble() || format_value.toInt(-1) != kThemeFileFormat)) {
    result.error = QCoreApplication::translate("ThemeFile",
                                               "Theme file format %1 is not supported by this build (expected %2).")
                       .arg(format_value.toVariant().toString())
                       .arg(kThemeFileFormat);
    return result;
  }
  const auto base = parse_base_token(object.value(QStringLiteral("base")).toString());
  if (!base) {
    result.error =
        QCoreApplication::translate("ThemeFile", "Theme file is missing a valid \"base\" (must be \"dark\" or \"light\").");
    return result;
  }

  // "roles" itself is optional (an absent one means "no overrides, just the
  // base scheme"); when present it must be an object, and its individual
  // entries are validated below.
  const auto roles_value = object.value(QStringLiteral("roles"));
  if (!roles_value.isUndefined() && !roles_value.isObject()) {
    result.error = QCoreApplication::translate("ThemeFile", "Theme file's \"roles\" is not an object.");
    return result;
  }

  CustomTheme custom_theme;
  custom_theme.base = *base;
  custom_theme.name = object.value(QStringLiteral("name")).toString();
  custom_theme.palette = theme(*base);  // seed: every role the file omits keeps this value.

  const auto& members = role_members();
  const auto roles_object = roles_value.toObject();
  for (auto it = roles_object.constBegin(); it != roles_object.constEnd(); ++it) {
    const auto member = members.find(it.key());
    if (member == members.end()) {
      result.warnings.push_back(
          QCoreApplication::translate("ThemeFile", "Unknown color role \"%1\" (ignored).").arg(it.key()));
      continue;
    }
    const auto color = parse_theme_color(it.value().toString());
    if (!color) {
      result.error = QCoreApplication::translate(
                         "ThemeFile", "Invalid color \"%1\" for role \"%2\" (expected #RRGGBB or #RRGGBBAA).")
                         .arg(it.value().toString(), it.key());
      result.warnings.clear();
      return result;
    }
    custom_theme.palette.*(member.value()) = *color;
  }

  result.theme = std::move(custom_theme);
  return result;
}

QByteArray serialize_theme_to_json(const ThemePalette& palette, ColorScheme base, const QString& name) {
  QJsonObject roles_object;
  for (const auto& [role_name, member] : theme_palette_roles()) {
    roles_object.insert(QString(role_name), color_to_hex(palette.*member));
  }

  QJsonObject object;
  object.insert(QStringLiteral("format"), kThemeFileFormat);
  object.insert(QStringLiteral("name"), name);
  object.insert(QStringLiteral("base"), base_token(base));
  object.insert(QStringLiteral("roles"), roles_object);
  return QJsonDocument(object).toJson(QJsonDocument::Indented);
}

bool is_bundled_theme_id(const QString& id) { return id.startsWith(kBundledThemeIdPrefix); }

QStringList bundled_theme_file_names() {
  // Listed in an authored order rather than alphabetically: the two neutral
  // brightness steps first, then the named palettes, then the accessibility one.
  static const QStringList names{
      QStringLiteral("darkest.patchytheme"),      QStringLiteral("medium-gray.patchytheme"),
      QStringLiteral("solarized-dark.patchytheme"), QStringLiteral("nord.patchytheme"),
      QStringLiteral("dracula.patchytheme"),      QStringLiteral("gruvbox-dark.patchytheme"),
      QStringLiteral("high-contrast.patchytheme"),
  };
  return names;
}

QString theme_file_path_for_id(const QString& id) {
  if (is_bundled_theme_id(id)) {
    ensure_theme_resources();
    return QStringLiteral(":/patchy/themes/") + id.mid(kBundledThemeIdPrefix.size());
  }
  const auto directory = user_themes_directory();
  return directory.isEmpty() ? QString() : QDir(directory).filePath(id);
}

ThemeLoadResult load_theme_by_id(const QString& id) {
  const auto path = theme_file_path_for_id(id);
  QFile file(path);
  if (path.isEmpty() || !file.open(QIODevice::ReadOnly)) {
    ThemeLoadResult result;
    result.error = QCoreApplication::translate("ThemeFile", "Theme file \"%1\" could not be read.").arg(id);
    return result;
  }
  return load_theme_from_json(file.readAll());
}

QString user_themes_directory() {
#ifdef Q_OS_WASM
  return {};
#else
  const auto override_dir = qEnvironmentVariable("PATCHY_THEMES_DIR");
  if (!override_dir.isEmpty()) {
    return QDir(override_dir).absolutePath();
  }
  const auto base = QStandardPaths::writableLocation(QStandardPaths::AppDataLocation);
  if (base.isEmpty()) {
    return {};
  }
  return base + QStringLiteral("/themes");
#endif
}

}  // namespace patchy::ui
