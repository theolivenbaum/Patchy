#pragma once

#include <QByteArray>
#include <QString>

#include <vector>

namespace patchy::ui {

// One mounted file system, as the OS reports it without touching the mount.
struct MountEntry {
  QString mount_point;  // absolute; "/" or no trailing slash
  QString file_system;  // OS type name: "apfs", "smbfs", "nfs4", "fuse.sshfs", "autofs"
  bool local{true};     // false for network file systems (macOS: MNT_LOCAL clear)
};

// File-system types Linux mounts across a network, plus automount triggers
// (`autofs`), whose first access mounts something and can block just as long.
[[nodiscard]] bool is_network_file_system_type(const QString& file_system);

// The entry whose mount point contains `path`, deepest one wins ("/" matches
// everything; "/Volumes/share" matches "/Volumes/share/x" but not
// "/Volumes/share2"). Null when no entry matches.
[[nodiscard]] const MountEntry* mount_for_path(const QString& path, const std::vector<MountEntry>& mounts);

// True when the mount holding `path` is a network file system or an automount
// trigger: a stat there can block for the whole network timeout.
[[nodiscard]] bool path_is_on_network_mount(const QString& path, const std::vector<MountEntry>& mounts);

// Parses the text of /proc/self/mounts (Linux). Exposed for tests.
[[nodiscard]] std::vector<MountEntry> parse_proc_mounts(const QByteArray& text);

// The live mount table, read from cached kernel data only (getmntinfo with
// MNT_NOWAIT on macOS, /proc/self/mounts on Linux): it never contacts a
// server. Empty on Windows (drive types answer there) and on wasm.
[[nodiscard]] std::vector<MountEntry> read_system_mounts();

}  // namespace patchy::ui
