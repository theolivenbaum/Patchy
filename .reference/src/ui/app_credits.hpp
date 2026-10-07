#pragma once

#include <QString>

namespace patchy::ui {

// Comma-joined rich-text GitHub links for everyone credited in the About dialog:
// code contributors and the reporters whose bug reports and suggestions were
// resolved; link_color is the anchor color for the site.
[[nodiscard]] QString contributors_link_html(const QString& link_color);

}  // namespace patchy::ui
