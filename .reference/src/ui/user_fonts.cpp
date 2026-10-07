#include "ui/user_fonts.hpp"

#include "formats/font_zip.hpp"
#include "ui/font_face_name_index.hpp"

#include <QCryptographicHash>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QFontDatabase>
#include <QHash>
#include <QSaveFile>
#include <QStandardPaths>

#include <cstdint>
#include <initializer_list>
#include <string>

#ifdef Q_OS_WASM
#include "ui/user_fonts_wasm.hpp"
#endif

namespace patchy::ui::user_fonts {
namespace {

bool has_any_suffix(const QString& path, std::initializer_list<const char*> suffixes) {
  const auto suffix = QFileInfo(path).suffix().toLower();
  for (const auto* candidate : suffixes) {
    if (suffix == QLatin1String(candidate)) {
      return true;
    }
  }
  return false;
}

// Content hashes of every font registered this session (drops plus the
// startup restore), so a re-dropped font counts as a duplicate instead of
// registering twice. Never pruned: application fonts are never removed. The
// value is the font's file name in the store (empty on wasm).
QHash<QByteArray, QString>& session_hashes() {
  static QHash<QByteArray, QString> hashes;
  return hashes;
}

#ifndef Q_OS_WASM
// Store files to delete at the next launch, one name per line. "Remove Added
// Fonts" cannot delete them on the spot: a FreeType font database (Linux, and
// the offscreen platform everywhere) opens the file again whenever it builds a
// new engine, so a font whose store copy is gone silently turns into another
// family the next time it is asked for at a new size.
constexpr auto kPendingRemovalFileName = ".remove-at-next-launch";

QString pending_removal_path(const QString& directory) {
  return directory + QLatin1Char('/') + QLatin1String(kPendingRemovalFileName);
}

QStringList read_pending_removals(const QString& directory) {
  QFile file(pending_removal_path(directory));
  if (!file.open(QIODevice::ReadOnly)) {
    return {};
  }
  QStringList names;
  for (const auto& line : QString::fromUtf8(file.readAll()).split(QLatin1Char('\n'))) {
    const auto name = line.trimmed();
    if (!name.isEmpty() && !names.contains(name)) {
      names.push_back(name);
    }
  }
  return names;
}

void write_pending_removals(const QString& directory, const QStringList& names) {
  const auto path = pending_removal_path(directory);
  if (names.isEmpty()) {
    QFile::remove(path);
    return;
  }
  QSaveFile file(path);
  if (!file.open(QIODevice::WriteOnly)) {
    return;
  }
  file.write(names.join(QLatin1Char('\n')).toUtf8());
  file.commit();
}

const QStringList& font_file_filters() {
  static const QStringList filters = {QStringLiteral("*.ttf"), QStringLiteral("*.otf"), QStringLiteral("*.ttc")};
  return filters;
}
#endif

// "f.ttf" -> "f (2).ttf" while the target exists: an already-registered file
// must never be overwritten in place (the running font may be backed by it).
QString unique_target_path(const QString& directory, const QString& name) {
  auto path = directory + QLatin1Char('/') + name;
  if (!QFileInfo::exists(path)) {
    return path;
  }
  const QFileInfo info(name);
  const auto stem = info.completeBaseName();
  const auto suffix = info.suffix();
  for (int counter = 2;; ++counter) {
    path = QStringLiteral("%1/%2 (%3)%4%5")
               .arg(directory, stem, QString::number(counter),
                    suffix.isEmpty() ? QString() : QStringLiteral("."), suffix);
    if (!QFileInfo::exists(path)) {
      return path;
    }
  }
}

struct RegisterOutcome {
  QStringList families;
  bool duplicate = false;
  bool ok = false;
};

// Writes the registration copy and registers it. Desktop: the copy in the
// user-fonts directory IS the persistence, and registering that copy (never
// the drop source) keeps the live font's backing file from vanishing. Wasm:
// the copy is MEMFS (session-only) and persistence is the IndexedDB put,
// skipped for restored fonts. Bytes that fail to register are never
// persisted.
RegisterOutcome register_font_bytes(const QString& name, const QByteArray& bytes,
                                    bool persist_to_wasm_store) {
  RegisterOutcome outcome;
  if (bytes.isEmpty() || name.isEmpty()) {
    return outcome;
  }
  const auto hash = QCryptographicHash::hash(bytes, QCryptographicHash::Sha256);
  if (const auto known = session_hashes().constFind(hash); known != session_hashes().constEnd()) {
#ifndef Q_OS_WASM
    // Adding a font again after "Remove Added Fonts" keeps it: its store copy
    // is still there, so it only has to come off the removal list.
    if (const auto directory = user_fonts_directory(); !directory.isEmpty() && !known->isEmpty()) {
      auto pending = read_pending_removals(directory);
      if (pending.removeAll(*known) > 0) {
        write_pending_removals(directory, pending);
      }
    }
#endif
    outcome.duplicate = true;
    outcome.ok = true;
    return outcome;
  }

#ifdef Q_OS_WASM
  static int counter = 0;
  const auto directory = QStringLiteral("/userfonts/%1").arg(++counter);
#else
  const auto directory = user_fonts_directory();
#endif
  if (directory.isEmpty() || !QDir().mkpath(directory)) {
    return outcome;
  }
  const auto path = unique_target_path(directory, name);
  {
    QFile file(path);
    if (!file.open(QIODevice::WriteOnly) || file.write(bytes) != bytes.size()) {
      return outcome;
    }
  }
  // Registered under the Windows names on macOS (ui/font_face_name_index.hpp): CoreText would
  // otherwise file a Bitstream "Futura BdCn BT" under Apple's "Futura", sharing one Qt style slot.
  const auto font_id = add_application_font_by_windows_names(path);
  if (font_id < 0) {
    QFile::remove(path);
    return outcome;
  }
  outcome.families = QFontDatabase::applicationFontFamilies(font_id);
  outcome.ok = true;
#ifdef Q_OS_WASM
  session_hashes().insert(hash, QString());
  if (persist_to_wasm_store) {
    wasm_store::put(QFileInfo(path).fileName(), bytes);
  }
#else
  session_hashes().insert(hash, QFileInfo(path).fileName());
  Q_UNUSED(persist_to_wasm_store);
#endif
  return outcome;
}

void fold_outcome_into_result(const RegisterOutcome& outcome, const QString& name,
                              AddFontsResult& result) {
  if (!outcome.ok) {
    result.invalid_names.push_back(name);
    return;
  }
  if (outcome.duplicate) {
    ++result.duplicate_count;
    return;
  }
  for (const auto& family : outcome.families) {
    if (!result.added_families.contains(family)) {
      result.added_families.push_back(family);
    }
  }
}

}  // namespace

bool is_user_font_path(const QString& path) {
  return has_any_suffix(path, {"ttf", "otf", "ttc"});
}

bool is_zip_path(const QString& path) {
  return has_any_suffix(path, {"zip"});
}

AddFontsResult add_user_fonts(const QStringList& paths) {
  AddFontsResult result;
  for (const auto& path : paths) {
    const QFileInfo info(path);
    QFile file(path);
    if (!file.open(QIODevice::ReadOnly)) {
      result.invalid_names.push_back(info.fileName());
      continue;
    }
    const auto bytes = file.readAll();
    if (is_zip_path(path)) {
      std::string error;
      const auto entries = formats::extract_font_files_from_zip(
          reinterpret_cast<const std::uint8_t*>(bytes.constData()),
          static_cast<std::size_t>(bytes.size()), {}, &error);
      if (entries.empty()) {
        result.zips_without_fonts.push_back(info.fileName());
        continue;
      }
      for (const auto& entry : entries) {
        const auto entry_name = QString::fromStdString(entry.name);
        const QByteArray entry_bytes(reinterpret_cast<const char*>(entry.bytes.data()),
                                     static_cast<qsizetype>(entry.bytes.size()));
        fold_outcome_into_result(register_font_bytes(entry_name, entry_bytes, true), entry_name,
                                 result);
      }
    } else if (is_user_font_path(path)) {
      fold_outcome_into_result(register_font_bytes(info.fileName(), bytes, true), info.fileName(),
                               result);
    }
  }
  return result;
}

QString user_fonts_directory() {
#ifdef Q_OS_WASM
  return {};
#else
  // Isolation knob, like PATCHY_SETTINGS_DIR: the UI suite gives every test process
  // its own store, because a process keeps its registered store files open and two
  // processes sharing one break each other (docs/fonts.md).
  if (const auto override_dir = qEnvironmentVariable("PATCHY_USER_FONTS_DIR"); !override_dir.isEmpty()) {
    return QDir::cleanPath(override_dir);
  }
  const auto base = QStandardPaths::writableLocation(QStandardPaths::AppDataLocation);
  if (base.isEmpty()) {
    return {};
  }
  return base + QStringLiteral("/user-fonts");
#endif
}

void restore_user_fonts_at_startup() {
#ifdef Q_OS_WASM
  wasm_store::begin_restore();
#else
  const auto directory = user_fonts_directory();
  if (directory.isEmpty()) {
    return;
  }
  // Nothing from the store is registered yet, so the files the last session
  // marked can go now.
  apply_pending_user_font_removals(directory);
  const QDir dir(directory);
  for (const auto& entry : dir.entryInfoList(font_file_filters(), QDir::Files, QDir::Name)) {
    QFile file(entry.absoluteFilePath());
    if (!file.open(QIODevice::ReadOnly)) {
      continue;
    }
    const auto hash = QCryptographicHash::hash(file.readAll(), QCryptographicHash::Sha256);
    if (session_hashes().contains(hash)) {
      continue;
    }
    if (add_application_font_by_windows_names(entry.absoluteFilePath()) >= 0) {
      session_hashes().insert(hash, entry.fileName());
    }
  }
#endif
}

void apply_pending_user_font_removals(const QString& directory) {
#ifdef Q_OS_WASM
  Q_UNUSED(directory);
#else
  if (directory.isEmpty()) {
    return;
  }
  QStringList still_pending;
  for (const auto& name : read_pending_removals(directory)) {
    // Names only: a list that somehow held a path must never reach outside the store.
    if (name.contains(QLatin1Char('/')) || name.contains(QLatin1Char('\\')) || name == QLatin1String("..")) {
      continue;
    }
    const auto path = directory + QLatin1Char('/') + name;
    if (QFileInfo::exists(path) && !QFile::remove(path)) {
      still_pending.push_back(name);  // locked or read-only: try again next launch
    }
  }
  write_pending_removals(directory, still_pending);
#endif
}

void clear_user_font_store() {
#ifdef Q_OS_WASM
  wasm_store::clear();
#else
  const auto directory = user_fonts_directory();
  if (directory.isEmpty()) {
    return;
  }
  // The registered fonts must stay usable until the app restarts, and some
  // font databases read the file again later (see kPendingRemovalFileName), so
  // the files are only marked here and deleted by the next launch.
  auto pending = read_pending_removals(directory);
  for (const auto& entry : QDir(directory).entryInfoList(font_file_filters(), QDir::Files, QDir::Name)) {
    if (!pending.contains(entry.fileName())) {
      pending.push_back(entry.fileName());
    }
  }
  write_pending_removals(directory, pending);
#endif
}

#ifdef Q_OS_WASM
void register_restored_font(const QString& name, const QByteArray& bytes) {
  (void)register_font_bytes(name, bytes, false);
}
#endif

}  // namespace patchy::ui::user_fonts
