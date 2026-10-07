#include "ui/splash_artwork.hpp"

#include "ui/action_icons.hpp"

#include <QPainter>
#include <QPaintEvent>

#include <algorithm>

namespace patchy::ui {

SplashArtwork::SplashArtwork(QWidget* parent) : QWidget(parent), logo_(patchy_app_icon()) {}

void SplashArtwork::paintEvent(QPaintEvent* event) {
  Q_UNUSED(event);
  QPainter painter(this);
  // The logo fills its canvas (taskbar icons need that), so the breathing room
  // is added here. Keep the brand colors in both schemes, and let QIcon
  // rasterize at the display's actual pixel density.
  const int side = std::min(width(), height()) * 824 / 1024;
  logo_.paint(&painter, QRect((width() - side) / 2, (height() - side) / 2, side, side));
}

}  // namespace patchy::ui
