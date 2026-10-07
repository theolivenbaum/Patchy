#include "ui/update_checker.hpp"

#include <QDateTime>
#include <QJsonDocument>
#include <QJsonObject>
#include <QNetworkAccessManager>
#include <QNetworkReply>
#include <QNetworkRequest>
#include <QMetaObject>
#include <QObject>
#include <QPointer>
#include <QTimer>
#include <QUrlQuery>
#include <QVariant>
#include <QtGlobal>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#if defined(Q_OS_WIN)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
#else
#include <netdb.h>
#include <sys/socket.h>
#include <sys/types.h>
#endif

namespace patchy::ui {
namespace {

// A getaddrinfo on a network that drops DNS queries takes 20 to 30 seconds. Qt runs
// QNetworkAccessManager's lookups on QHostInfo's thread pool, and ~QCoreApplication
// waits for that pool with no timeout, so a lookup still pending at quit froze the
// app after its window had closed (GitHub issue 48, macOS 26). The host is therefore
// resolved on a detached thread first, which nothing waits for, and the request only
// starts once the resolver has answered (fast, and cached for Qt's own lookup).
constexpr int kHostResolveTimeoutMs = 10000;

struct HostResolveState {
  std::mutex mutex;
  QObject* target{nullptr};  // the owner while it lives; null once destroyed or timed out
  bool finished{false};
};

bool host_name_resolves(const std::string& host) {
#if defined(Q_OS_WIN)
  WSADATA wsa_data{};
  if (WSAStartup(MAKEWORD(2, 2), &wsa_data) != 0) {
    return false;
  }
#endif
  addrinfo hints{};
  hints.ai_socktype = SOCK_STREAM;
  addrinfo* result = nullptr;
  const int rc = getaddrinfo(host.c_str(), nullptr, &hints, &result);
  if (result != nullptr) {
    freeaddrinfo(result);
  }
#if defined(Q_OS_WIN)
  WSACleanup();
#endif
  return rc == 0;
}

std::optional<std::vector<std::int64_t>> parse_dotted_version(QString version) {
  version = version.trimmed();
  if (version.startsWith(QLatin1Char('v'), Qt::CaseInsensitive)) {
    version.remove(0, 1);
  }
  if (version.isEmpty()) {
    return std::nullopt;
  }

  std::vector<std::int64_t> parts;
  for (const auto& part : version.split(QLatin1Char('.'))) {
    if (part.isEmpty()) {
      return std::nullopt;
    }
    bool ok = false;
    const auto value = part.toLongLong(&ok);
    if (!ok || value < 0) {
      return std::nullopt;
    }
    parts.push_back(value);
  }
  return parts;
}

bool is_download_url_usable(const QUrl& url) {
  if (!url.isValid() || url.isRelative()) {
    return false;
  }
  const auto scheme = url.scheme();
  return scheme == QStringLiteral("http") || scheme == QStringLiteral("https");
}

QUrl cache_busted_manifest_url() {
  auto url = update_manifest_url();
  QUrlQuery query(url);
  query.addQueryItem(QStringLiteral("patchy_check"), QString::number(QDateTime::currentSecsSinceEpoch()));
  url.setQuery(query);
  return url;
}

}  // namespace

bool update_checks_available() {
#if defined(Q_OS_WASM) || defined(PATCHY_STORE_BUILD)
  return false;
#else
  return qEnvironmentVariableIntValue("PATCHY_NO_UPDATE_CHECK") == 0;
#endif
}

QString current_update_platform() {
#if defined(Q_OS_WIN)
  return QStringLiteral("windows");
#elif defined(Q_OS_MACOS)
  return QStringLiteral("macos");
#elif defined(Q_OS_LINUX)
  return QStringLiteral("linux");
#else
  return {};
#endif
}

QUrl update_manifest_url() {
  // Test and diagnostics knob: point the startup check at another manifest (a local
  // server, or an unresolvable host to hold a lookup open; see docs/platform.md).
  const auto override_url = qEnvironmentVariable("PATCHY_UPDATE_MANIFEST_URL");
  if (!override_url.isEmpty()) {
    return QUrl(override_url);
  }
  return QUrl(QStringLiteral("https://raw.githubusercontent.com/SethRobinson/Patchy/main/latest_version.json"));
}

bool update_version_is_newer(const QString& latest_version, const QString& current_version) {
  const auto latest_parts = parse_dotted_version(latest_version);
  const auto current_parts = parse_dotted_version(current_version);
  if (!latest_parts.has_value() || !current_parts.has_value()) {
    return false;
  }

  const auto count = std::max(latest_parts->size(), current_parts->size());
  for (std::size_t index = 0; index < count; ++index) {
    const auto latest_part = index < latest_parts->size() ? (*latest_parts)[index] : 0;
    const auto current_part = index < current_parts->size() ? (*current_parts)[index] : 0;
    if (latest_part != current_part) {
      return latest_part > current_part;
    }
  }
  return false;
}

std::optional<UpdateInfo> parse_update_manifest(const QByteArray& json, const QString& platform,
                                                const QString& current_version) {
  return inspect_update_manifest(json, platform, current_version).update;
}

UpdateCheckResult inspect_update_manifest(const QByteArray& json, const QString& platform,
                                          const QString& current_version) {
  UpdateCheckResult result;
  result.platform = platform;

  if (platform.isEmpty()) {
    result.status = UpdateCheckStatus::UnsupportedPlatform;
    return result;
  }

  QJsonParseError parse_error;
  const auto document = QJsonDocument::fromJson(json, &parse_error);
  if (parse_error.error != QJsonParseError::NoError || !document.isObject()) {
    result.status = UpdateCheckStatus::InvalidManifest;
    result.detail = parse_error.errorString();
    return result;
  }

  const auto platforms = document.object().value(QStringLiteral("platforms")).toObject();
  if (platforms.isEmpty()) {
    result.status = UpdateCheckStatus::InvalidManifest;
    result.detail = QStringLiteral("missing platforms");
    return result;
  }
  const auto platform_entry = platforms.value(platform).toObject();
  if (platform_entry.isEmpty()) {
    result.status = UpdateCheckStatus::MissingPlatform;
    return result;
  }

  const auto latest_version = platform_entry.value(QStringLiteral("version")).toString().trimmed();
  const auto download_url = QUrl(platform_entry.value(QStringLiteral("download_url")).toString().trimmed());
  result.latest_version = latest_version;
  if (latest_version.isEmpty() || !parse_dotted_version(latest_version).has_value() ||
      !parse_dotted_version(current_version).has_value()) {
    result.status = UpdateCheckStatus::InvalidVersion;
    return result;
  }
  if (!is_download_url_usable(download_url)) {
    result.status = UpdateCheckStatus::InvalidDownloadUrl;
    return result;
  }

  if (!update_version_is_newer(latest_version, current_version)) {
    result.status = UpdateCheckStatus::NoUpdateAvailable;
    return result;
  }

  result.status = UpdateCheckStatus::UpdateAvailable;
  result.update = UpdateInfo{platform, latest_version, download_url};
  return result;
}

namespace {

UpdateCheckResult network_error_result() {
  UpdateCheckResult result;
  result.status = UpdateCheckStatus::NetworkError;
  result.platform = current_update_platform();
  return result;
}

void start_manifest_request(const QPointer<QObject>& owner_guard, const QUrl& url, QString current_version,
                            UpdateCheckResultCallback callback) {
  QObject* owner = owner_guard.data();
  if (owner == nullptr) {
    return;
  }
  auto* manager = new QNetworkAccessManager(owner);
  QNetworkRequest request(url);
  request.setAttribute(QNetworkRequest::RedirectPolicyAttribute, QNetworkRequest::NoLessSafeRedirectPolicy);
  request.setAttribute(QNetworkRequest::CacheLoadControlAttribute, QNetworkRequest::AlwaysNetwork);
  request.setRawHeader("Cache-Control", "no-cache");
  request.setRawHeader("Pragma", "no-cache");
  request.setTransferTimeout(10000);

  auto* reply = manager->get(request);
  QObject::connect(reply, &QNetworkReply::finished, owner,
                   [reply, manager, owner_guard, current_version = std::move(current_version),
                    callback = std::move(callback)]() mutable {
                     UpdateCheckResult result;
                     if (reply->error() == QNetworkReply::NoError) {
                       result = inspect_update_manifest(reply->readAll(), current_update_platform(), current_version);
                     } else {
                       result = network_error_result();
                       result.detail = reply->errorString();
                       result.http_status =
                           reply->attribute(QNetworkRequest::HttpStatusCodeAttribute).toInt();
                     }
                     if (owner_guard != nullptr) {
                       callback(std::move(result));
                     }
                     reply->deleteLater();
                     manager->deleteLater();
                   });
}

}  // namespace

void request_update_check(QObject* owner, QString current_version, UpdateCheckResultCallback callback) {
  if (owner == nullptr || !callback) {
    return;
  }
  const QPointer<QObject> owner_guard(owner);
  const auto url = cache_busted_manifest_url();
  const auto host = QUrl::toAce(url.host());
#if defined(Q_OS_WASM) && !defined(__EMSCRIPTEN_PTHREADS__)
  const bool can_resolve_first = false;  // no threads; the wasm app never runs the check anyway
#else
  const bool can_resolve_first = !host.isEmpty();
#endif
  if (!can_resolve_first) {
    start_manifest_request(owner_guard, url, std::move(current_version), std::move(callback));
    return;
  }

  auto state = std::make_shared<HostResolveState>();
  state->target = owner;
  // Direct (context-less) call from ~QObject: once the owner is gone the resolver
  // thread must not post to it. Anything it posted earlier dies with the owner.
  QObject::connect(owner, &QObject::destroyed, [state] {
    const std::lock_guard<std::mutex> lock(state->mutex);
    state->target = nullptr;
  });
  // A resolver that is still silent after this long reports the check as failed and
  // is abandoned; the thread ends on its own whenever the OS gives up.
  QTimer::singleShot(kHostResolveTimeoutMs, owner, [state, callback] {
    {
      const std::lock_guard<std::mutex> lock(state->mutex);
      if (state->finished) {
        return;
      }
      state->finished = true;
      state->target = nullptr;
    }
    callback(network_error_result());
  });
  std::thread([state, host = std::string(host.constData(), static_cast<std::size_t>(host.size())), owner_guard, url,
               current_version = std::move(current_version), callback = std::move(callback)]() mutable {
    const bool resolved = host_name_resolves(host);
    const std::lock_guard<std::mutex> lock(state->mutex);
    if (state->finished || state->target == nullptr) {
      return;
    }
    state->finished = true;
    QMetaObject::invokeMethod(
        state->target,
        [resolved, owner_guard, url, current_version = std::move(current_version),
         callback = std::move(callback)]() mutable {
          if (owner_guard == nullptr) {
            return;
          }
          if (resolved) {
            start_manifest_request(owner_guard, url, std::move(current_version), std::move(callback));
          } else {
            callback(network_error_result());
          }
        },
        Qt::QueuedConnection);
  }).detach();
}

QString update_check_status_text(const UpdateCheckResult& result) {
  switch (result.status) {
    case UpdateCheckStatus::UpdateAvailable:
      if (result.update.has_value()) {
        return QObject::tr("Update available: Patchy %1.").arg(result.update->version);
      }
      return QObject::tr("Update available.");
    case UpdateCheckStatus::NoUpdateAvailable:
      if (!result.latest_version.isEmpty()) {
        return QObject::tr("Patchy is up to date (%1).").arg(result.latest_version);
      }
      return QObject::tr("Patchy is up to date.");
    case UpdateCheckStatus::UnsupportedPlatform:
      return QObject::tr("Update checks are not supported on this platform.");
    case UpdateCheckStatus::MissingPlatform:
      if (!result.platform.isEmpty()) {
        return QObject::tr("Update check failed: no manifest entry for %1.").arg(result.platform);
      }
      return QObject::tr("Update check failed: no manifest entry for this platform.");
    case UpdateCheckStatus::InvalidManifest:
      return QObject::tr("Update check failed: invalid update manifest.");
    case UpdateCheckStatus::InvalidVersion:
      return QObject::tr("Update check failed: invalid version data.");
    case UpdateCheckStatus::InvalidDownloadUrl:
      return QObject::tr("Update check failed: invalid download URL.");
    case UpdateCheckStatus::NetworkError:
      if (result.http_status > 0 && !result.detail.isEmpty()) {
        return QObject::tr("Update check failed: HTTP %1 (%2).").arg(result.http_status).arg(result.detail);
      }
      if (result.http_status > 0) {
        return QObject::tr("Update check failed: HTTP %1.").arg(result.http_status);
      }
      if (!result.detail.isEmpty()) {
        return QObject::tr("Update check failed: %1.").arg(result.detail);
      }
      return QObject::tr("Update check failed.");
  }
  return QObject::tr("Update check failed.");
}

}  // namespace patchy::ui
