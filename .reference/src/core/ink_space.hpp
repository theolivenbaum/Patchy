#pragma once

#include "core/layer.hpp"

#include <array>
#include <cstdint>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

namespace patchy {

// A CMYK document's ink space, as two lookup tables sampled from its ICC profile: sRGB
// to the four inks and the inks back to sRGB. (A grayscale document with a gray profile
// gets the one-channel form of the same thing; see is_gray below.)
//
// Patchy edits in RGB: a CMYK file's pixels are converted when it is read. Its adjustment
// layers are not pixels, though. Photoshop evaluates them on the ink channels, and the
// same Levels or Curves numbers applied to RGB give a visibly different picture (a
// psd-tools Levels fixture matched Photoshop on 28 percent of pixels that way, and on
// 99.9 percent through these tables). An adjustment that carries an ink space takes its
// input back to the inks, maps each ink through its channel's curve, and returns to sRGB.
//
// Ink values use the PSD storage convention throughout: 0 = full ink, 255 = none.
// The tables are plain data so this stays in core; color/color_management builds them.
struct InkSpace {
  // Stable for one profile (a hash of its bytes): layer metadata names the space by it.
  std::string id;
  // rgb_to_ink: rgb_grid^3 nodes of 4 inks, red varying slowest. ink_to_rgb: ink_grid^4
  // nodes of 3 components, cyan varying slowest. Both hold 16-bit samples of 0..65535.
  int rgb_grid{0};
  int ink_grid{0};
  std::vector<std::uint16_t> rgb_to_ink;
  std::vector<std::uint16_t> ink_to_rgb;

  // A grayscale document's space instead: its one channel through the gray profile
  // (Dot Gain 20%, Gray Gamma 2.2, ...). gray_to_rgb holds the sRGB triple for each of
  // the 256 stored gray values, rgb_to_gray the stored value whose triple is nearest a
  // given sRGB level. When these are filled the four tables above are empty.
  std::vector<std::uint8_t> gray_to_rgb;
  std::vector<std::uint8_t> rgb_to_gray;

  [[nodiscard]] bool is_gray() const noexcept { return gray_to_rgb.size() == 768U && rgb_to_gray.size() == 256U; }
  [[nodiscard]] std::uint8_t gray_from_rgb(RgbColor color) const noexcept { return rgb_to_gray[color.green]; }
  [[nodiscard]] RgbColor rgb_from_gray(std::uint8_t gray) const noexcept {
    const auto* triple = gray_to_rgb.data() + static_cast<std::size_t>(gray) * 3U;
    return RgbColor{triple[0], triple[1], triple[2]};
  }
  [[nodiscard]] bool valid() const noexcept;
  // Trilinear / quadrilinear interpolation between the sampled nodes.
  [[nodiscard]] std::array<std::uint8_t, 4> ink_from_rgb(RgbColor color) const noexcept;
  [[nodiscard]] RgbColor rgb_from_ink(const std::array<std::uint8_t, 4>& ink) const noexcept;
};

// The ink spaces of the documents opened in this process, by id. A layer's metadata can
// only carry the id; the tables are found here when its settings are read back. A space
// that is not registered (the document was saved as RGB and reopened in another run)
// simply leaves the adjustment on its RGB math.
void register_ink_space(std::shared_ptr<const InkSpace> space);
[[nodiscard]] std::shared_ptr<const InkSpace> find_ink_space(std::string_view id);

}  // namespace patchy
